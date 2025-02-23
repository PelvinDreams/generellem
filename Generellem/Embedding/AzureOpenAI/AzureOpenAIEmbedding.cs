﻿using Azure;
using Azure.AI.OpenAI;

using Generellem.Document.DocumentTypes;
using Generellem.Llm;
using Generellem.Rag;
using Generellem.Services;
using Generellem.Services.Exceptions;

using Microsoft.Extensions.Logging;

using Polly;

namespace Generellem.Embedding.AzureOpenAI;

public class AzureOpenAIEmbedding(
    IDynamicConfiguration config, 
    LlmClientFactory llmClientFact, 
    ILogger<AzureOpenAIEmbedding> logger)
    : IEmbedding
{
    readonly OpenAIClient openAIClient = llmClientFact.CreateOpenAIClient();

    readonly ResiliencePipeline pipeline =
    new ResiliencePipelineBuilder()
        .AddRetry(new()
        {
            ShouldHandle = new PredicateBuilder().Handle<Exception>(ex => ex is not GenerellemNeedsIngestionException)
        })
        .AddTimeout(TimeSpan.FromSeconds(7))
        .Build();

    /// <summary>
    /// Breaks text into chunks and adds an embedding to each chunk based on the text in that chunk.
    /// </summary>
    /// <param name="fullText">Full document text.</param>
    /// <param name="docType"><see cref="IDocumentType"/> for extracting text from document.</param>
    /// <param name="documentReference">Reference to file. e.g. either a path, url, or some other indicator of where the file came from.</param>
    /// <param name="cancellationToken"><see cref="CancellationToken"/></param>
    /// <returns>List of <see cref="TextChunk"/></returns>
    public virtual async Task<List<TextChunk>> EmbedAsync(string fullText, IDocumentType docType, string documentReference, CancellationToken cancellationToken)
    {
        List<TextChunk> chunks = TextProcessor.BreakIntoChunks(fullText, documentReference);

        foreach (TextChunk chunk in chunks)
        {
            if (chunk.Content is null) continue;

            try
            {
                EmbeddingsOptions embeddingsOptions = GetEmbeddingOptions(chunk.Content);
                Response<Embeddings> embeddings = await pipeline.ExecuteAsync<Response<Embeddings>>(
                    async token => await openAIClient.GetEmbeddingsAsync(embeddingsOptions, token),
                cancellationToken);

                chunk.Embedding = embeddings.Value.Data[0].Embedding;
            }
            catch (RequestFailedException rfEx)
            {
                logger.LogError(GenerellemLogEvents.AuthorizationFailure, rfEx, "Please check credentials and exception details for more info.");
                throw;
            }
        }

        return chunks;
    }

    /// <summary>
    /// Embedding Options for Azure Search.
    /// </summary>
    /// <param name="text">Text string for calculating options.</param>
    /// <returns><see cref="EmbeddingsOptions"/></returns>
    public EmbeddingsOptions GetEmbeddingOptions(string text)
    {
        string? embeddingName = config[GKeys.AzOpenAIEmbeddingName];
        ArgumentException.ThrowIfNullOrWhiteSpace(embeddingName, nameof(embeddingName));

        EmbeddingsOptions embeddingsOptions = new(embeddingName, new string[] { text });

        return embeddingsOptions;
    }
}
