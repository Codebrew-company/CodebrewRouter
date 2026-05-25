namespace Blaze.LlmGateway.Api;

public static class ConversationsEndpoint
{
    public static async Task<ConversationObject> CreateAsync(
        CreateConversationRequest request,
        IProtocolStore store,
        CancellationToken cancellationToken)
    {
        var conversation = ConversationObject.Create(metadata: request.Metadata);
        await store.SaveConversationAsync(conversation, cancellationToken);

        if (request.Items is { Count: > 0 })
        {
            await store.AddConversationItemsAsync(conversation.Id, request.Items, cancellationToken);
        }

        return conversation;
    }

    public static async Task<IResult> GetAsync(string conversationId, IProtocolStore store, CancellationToken cancellationToken)
        => await store.GetConversationAsync(conversationId, cancellationToken) is { } conversation
            ? Results.Json(conversation)
            : Results.NotFound(new ErrorResponse(new ErrorDetail($"Conversation '{conversationId}' was not found.", "not_found", "conversation_not_found")));

    public static async Task<IResult> UpdateAsync(
        string conversationId,
        UpdateConversationRequest request,
        IProtocolStore store,
        CancellationToken cancellationToken)
    {
        var existing = await store.GetConversationAsync(conversationId, cancellationToken);
        if (existing is null)
        {
            return Results.NotFound(new ErrorResponse(new ErrorDetail($"Conversation '{conversationId}' was not found.", "not_found", "conversation_not_found")));
        }

        var updated = existing with { Metadata = request.Metadata ?? existing.Metadata };
        await store.SaveConversationAsync(updated, cancellationToken);
        return Results.Json(updated);
    }

    public static async Task<IResult> DeleteAsync(string conversationId, IProtocolStore store, CancellationToken cancellationToken)
        => await store.DeleteConversationAsync(conversationId, cancellationToken)
            ? Results.Json(new { id = conversationId, @object = "conversation.deleted", deleted = true })
            : Results.NotFound(new ErrorResponse(new ErrorDetail($"Conversation '{conversationId}' was not found.", "not_found", "conversation_not_found")));

    public static async Task<ConversationItemsList> AddItemsAsync(
        string conversationId,
        CreateConversationItemsRequest request,
        IProtocolStore store,
        CancellationToken cancellationToken)
    {
        await store.AddConversationItemsAsync(conversationId, request.Items, cancellationToken);
        var items = await store.ListConversationItemsAsync(conversationId, cancellationToken: cancellationToken);
        return new ConversationItemsList("list", [.. items]);
    }

    public static async Task<ConversationItemsList> ListItemsAsync(
        string conversationId,
        int? limit,
        string? after,
        string? order,
        IProtocolStore store,
        CancellationToken cancellationToken)
    {
        var items = await store.ListConversationItemsAsync(conversationId, limit, after, order, cancellationToken);
        return new ConversationItemsList("list", [.. items]);
    }

    public static async Task<IResult> GetItemAsync(
        string conversationId,
        string itemId,
        IProtocolStore store,
        CancellationToken cancellationToken)
        => await store.GetConversationItemAsync(conversationId, itemId, cancellationToken) is { } item
            ? Results.Json(item)
            : Results.NotFound(new ErrorResponse(new ErrorDetail($"Conversation item '{itemId}' was not found.", "not_found", "conversation_item_not_found")));

    public static async Task<IResult> DeleteItemAsync(
        string conversationId,
        string itemId,
        IProtocolStore store,
        CancellationToken cancellationToken)
        => await store.DeleteConversationItemAsync(conversationId, itemId, cancellationToken)
            ? Results.Json(new { id = itemId, @object = "conversation.item.deleted", deleted = true })
            : Results.NotFound(new ErrorResponse(new ErrorDetail($"Conversation item '{itemId}' was not found.", "not_found", "conversation_item_not_found")));
}
