namespace VeruSuite.Api;

/// <summary>Who the key acts as, and the people around them.</summary>
public sealed class IdentityClient
{
    private readonly VeruApiClient _api;

    internal IdentityClient(VeruApiClient api) => _api = api;

    /// <summary>The profile of the user this key acts as.</summary>
    /// <remarks>
    /// The cheapest way to check a key works: one call, and it needs only
    /// identity.read.
    /// </remarks>
    public async Task<Profile> MeAsync(CancellationToken cancellationToken = default)
    {
        var (data, _) = await _api.SendAsync<Profile>(
            HttpMethod.Get, "/v1/me", cancellationToken: cancellationToken).ConfigureAwait(false);
        return data ?? new Profile();
    }

    /// <summary>One page of the workspace's groups, ordered by name.</summary>
    /// <remarks>The directory behind a people picker, not the administrative view.</remarks>
    public async Task<(IReadOnlyList<Group> Groups, Meta Meta)> ListGroupsAsync(
        int limit = 0,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        var query = new List<KeyValuePair<string, object?>>();
        if (limit > 0) query.Add(new("limit", limit));
        if (!string.IsNullOrEmpty(cursor)) query.Add(new("cursor", cursor));

        var (data, meta) = await _api.SendAsync<List<Group>>(
            HttpMethod.Get, "/v1/groups", query, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return (data ?? new List<Group>(), meta);
    }

    /// <summary>Every group, following the cursor.</summary>
    public IAsyncEnumerable<Group> GroupsAsync(CancellationToken cancellationToken = default) =>
        _api.PaginateAsync<Group>("/v1/groups", null, cancellationToken);
}

/// <summary>Address books and the people in them.</summary>
public sealed class ContactsClient
{
    private readonly VeruApiClient _api;

    internal ContactsClient(VeruApiClient api) => _api = api;

    /// <summary>Every book in the caller's mailbox.</summary>
    /// <remarks>Not paged: there are a handful, so a loop would never run twice.</remarks>
    public async Task<IReadOnlyList<AddressBook>> ListAddressBooksAsync(
        CancellationToken cancellationToken = default)
    {
        var (data, _) = await _api.SendAsync<List<AddressBook>>(
            HttpMethod.Get, "/v1/address-books", cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return data ?? new List<AddressBook>();
    }

    /// <summary>One page of contacts.</summary>
    /// <remarks>
    /// <c>Q</c> matches the name and every address on a contact, including
    /// secondary ones, so somebody's old address still finds them.
    /// </remarks>
    public async Task<(IReadOnlyList<Contact> Contacts, Meta Meta)> ListContactsAsync(
        ListContactsQuery? query = null,
        CancellationToken cancellationToken = default)
    {
        var (data, meta) = await _api.SendAsync<List<Contact>>(
            HttpMethod.Get, "/v1/contacts", ContactQuery(query), cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return (data ?? new List<Contact>(), meta);
    }

    /// <summary>Every contact matching the filter, following the cursor.</summary>
    public IAsyncEnumerable<Contact> ContactsAsync(
        ListContactsQuery? query = null,
        CancellationToken cancellationToken = default) =>
        _api.PaginateAsync<Contact>("/v1/contacts", ContactQuery(query), cancellationToken);

    /// <summary>One contact.</summary>
    public async Task<Contact> GetContactAsync(string id, CancellationToken cancellationToken = default)
    {
        var (data, _) = await _api.SendAsync<Contact>(
            HttpMethod.Get, "/v1/contacts/" + Uri.EscapeDataString(id),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return data ?? new Contact();
    }

    /// <summary>Adds a contact to an address book.</summary>
    /// <remarks>
    /// <c>Name</c> is the only required field. A field this API does not
    /// publish is refused rather than ignored, so a typo is a 400 here rather
    /// than a value that silently never arrived.
    /// </remarks>
    public async Task<Contact> CreateContactAsync(
        Contact contact,
        CancellationToken cancellationToken = default)
    {
        var (data, _) = await _api.SendAsync<Contact>(
            HttpMethod.Post, "/v1/contacts", body: contact, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return data ?? new Contact();
    }

    /// <summary>Edits a contact.</summary>
    /// <remarks>
    /// Fields left null are left alone, but a list that is sent replaces the
    /// whole list rather than adding to it: read the contact first if you mean
    /// to append an address.
    /// </remarks>
    public async Task<Contact> UpdateContactAsync(
        string id,
        Contact contact,
        CancellationToken cancellationToken = default)
    {
        var (data, _) = await _api.SendAsync<Contact>(
            HttpMethod.Patch, "/v1/contacts/" + Uri.EscapeDataString(id), body: contact,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return data ?? new Contact();
    }

    /// <summary>Removes a contact from its address book.</summary>
    public Task DeleteContactAsync(string id, CancellationToken cancellationToken = default) =>
        _api.SendAsync<object>(
            HttpMethod.Delete, "/v1/contacts/" + Uri.EscapeDataString(id),
            cancellationToken: cancellationToken);

    private static List<KeyValuePair<string, object?>>? ContactQuery(ListContactsQuery? query)
    {
        if (query is null) return null;

        var q = new List<KeyValuePair<string, object?>>();
        if (!string.IsNullOrEmpty(query.AddressBookId)) q.Add(new("address_book_id", query.AddressBookId));
        if (!string.IsNullOrEmpty(query.Q)) q.Add(new("q", query.Q));
        if (query.Limit > 0) q.Add(new("limit", query.Limit));
        if (!string.IsNullOrEmpty(query.Cursor)) q.Add(new("cursor", query.Cursor));
        return q.Count == 0 ? null : q;
    }
}
