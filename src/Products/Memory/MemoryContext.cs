﻿using Microsoft.EntityFrameworkCore;
using SearchEntities;
using DataEntities;
using OpenAI.Chat;
using OpenAI.Embeddings;
using VectorEntities;
using Microsoft.SemanticKernel.Connectors.InMemory;
using Microsoft.Extensions.VectorData;
using Newtonsoft.Json;
using Products.Models;

namespace Products.Memory;

public class MemoryContext
{
    private ILogger _logger;
    public ChatClient _chatClient;
    public EmbeddingClient _embeddingClient;
    public IVectorStoreRecordCollection<int, ProductVector> _productsCollection;
    private string _systemPrompt = "";
    private bool _isMemoryCollectionInitialized = false;

    public async Task InitMemoryContextAsync(ILogger logger, IConfiguration config, Context db, ChatClient chatClient, EmbeddingClient embeddingClient)
    {
        _logger = logger;
        _chatClient = chatClient;
        _embeddingClient = embeddingClient;


        var vectorProductStore = new InMemoryVectorStore();
        _productsCollection = vectorProductStore.GetCollection<int, ProductVector>("products");
        await _productsCollection.CreateCollectionIfNotExistsAsync();

        // create chat history
        _systemPrompt = "You are a useful assistant. You always reply with a short and funny message. If you do not know an answer, you say 'I don't know that.' You only answer questions related to outdoor camping products. For any other type of questions, explain to the user that you only answer outdoor camping products questions. Do not store memory of the chat conversation.";
    }

    internal async void FillMemoryProducts(Context db) 
    { 
        _logger.LogInformation("Get a copy of the list of products");
        // get a copy of the list of products
        var products = await db.Product.ToListAsync();

        _logger.LogInformation("Filling products in memory");

        // iterate over the products and add them to the memory
        foreach (var product in products)
        {
            _logger.LogInformation("Adding product to memory: {Product}", product.Name);
            var productInfo = $"[{product.Name}] is a product that costs [{product.Price}] and is described as [{product.Description}]";

            // new product vector
            var productVector = new ProductVector
            {
                Id = product.Id,
                Name = product.Name,
                Description = product.Description,
                Price = product.Price,
                ImageUrl = product.ImageUrl
            };
            var result = await _embeddingClient.GenerateEmbeddingAsync(productInfo);
            productVector.Vector = result.Value.ToFloats();
            var recordId = await _productsCollection.UpsertAsync(productVector);
            _logger.LogInformation("Product added to memory: {Product} with recordId: {RecordId}", product.Name, recordId);
        }

        _logger.LogInformation("DONE! Filling products in memory");
    }

    public async Task<SearchResponse> Search(string search, Context db)
    {
        if(!_isMemoryCollectionInitialized)
        {
            FillMemoryProducts(db);
            _isMemoryCollectionInitialized = true;
        }

        var response = new SearchResponse();
        Product? firstProduct = null;
        var responseText = "";
        try
        {
            var result = await _embeddingClient.GenerateEmbeddingAsync(search);
            var vectorSearchQuery = result.Value.ToFloats();

            var searchOptions = new VectorSearchOptions()
            {
                Top = 1,
                VectorPropertyName = "Vector"
            };

            // search the vector database for the most similar product        
            var searchResults = await _productsCollection.VectorizedSearchAsync(vectorSearchQuery, searchOptions);

            await foreach (var searchItem in searchResults.Results)
            {
                if (searchItem.Score > 0.4)
                {
                    // product found, search the db for the product details                    
                    firstProduct = new Product
                    {
                        Id = searchItem.Record.Id,
                        Name = searchItem.Record.Name,
                        Description = searchItem.Record.Description,
                        Price = searchItem.Record.Price,
                        ImageUrl = searchItem.Record.ImageUrl
                    };

                    responseText = $"The product [{firstProduct.Name}] fits with the search criteria [{search}][{searchItem.Score.Value.ToString("0.00")}]";
                }
            }

            // let's improve the response message
            var prompt = @$"You are an intelligent assistant helping Contoso Inc clients with their search about outdoor product. Generate a catchy and friendly message using the following information:
    - User Question: {search}
    - Found Product Name: {firstProduct.Name}
    - Found Product Description: {firstProduct.Description}
    - Found Product Price: {firstProduct.Price}
Include the found product information in the response to the user question.";

            var messages = new List<ChatMessage>
    {
        new SystemChatMessage(_systemPrompt),
        new UserChatMessage(prompt)
    };

            _logger.LogInformation("Chat history created: {ChatHistory}", JsonConvert.SerializeObject(messages));

            var resultPrompt = await _chatClient.CompleteChatAsync(messages);
            responseText = resultPrompt.Value.Content[0].Text!;

            // create a response object
            response = new SearchResponse
            {
                Products = firstProduct == null ? [new Product()] : [firstProduct],
                Response = responseText
            };

        }
        catch (Exception ex)
        {
            // Handle exceptions (log them, rethrow, etc.)
            response.Response = $"An error occurred: {ex.Message}";
        }
        return response;
    }
}

public static class Extensions
{
    public static void InitSemanticMemory(this IHost host)
    {
        using var scope = host.Services.CreateScope();
        var services = scope.ServiceProvider;
        var context = services.GetRequiredService<MemoryContext>();
        context.InitMemoryContextAsync(
            services.GetRequiredService<ILogger<Program>>(),
            services.GetRequiredService<IConfiguration>(),
            services.GetRequiredService<Context>(),
            services.GetRequiredService<ChatClient>(),
            services.GetRequiredService<EmbeddingClient>());
    }
}
