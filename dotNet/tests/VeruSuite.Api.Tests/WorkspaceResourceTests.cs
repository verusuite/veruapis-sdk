using VeruSuite.Api;
using Xunit;

namespace VeruSuite.Api.Tests;

/// <summary>The workspace surfaces: who the key is, and who else is here.</summary>
public class IdentityAndContactsTests
{
    [Fact]
    public async Task Me_reads_the_profile()
    {
        using var api = new FakeApi(new Reply(200, Bodies.Envelope(
            @"{""id"":""u1"",""email"":""alice@acme.example"",""name"":""Alice"",
               ""role"":""tenant_admin"",""workspace"":{""id"":""w1""},
               ""mailbox_address"":""alice@acme.example""}")));

        var me = await api.Client().Identity.MeAsync();

        Assert.Equal("GET /v1/me", api.Last.Route);
        Assert.Equal("Alice", me.Name);
        Assert.Equal("w1", me.Workspace.Id);
    }

    [Fact]
    public async Task ListGroups_keeps_a_withheld_count_distinguishable_from_zero()
    {
        using var api = new FakeApi(new Reply(200, Bodies.Envelope(
            @"[{""id"":""g1"",""name"":""Execs"",""member_count"":null},
               {""id"":""g2"",""name"":""Design"",""member_count"":0}]")));

        var (groups, _) = await api.Client().Identity.ListGroupsAsync(25, "c2");

        Assert.Equal("GET /v1/groups", api.Last.Route);
        Assert.Equal(["25"], api.Last.Query["limit"]);
        Assert.Equal(["c2"], api.Last.Query["cursor"]);

        // null means the caller cannot see into the group, and 0 means it is
        // empty. Flattening the first into the second would report that a team
        // has nobody in it.
        Assert.Null(groups[0].MemberCount);
        Assert.Equal(0, groups[1].MemberCount);
    }

    [Fact]
    public async Task Groups_walks_the_pages()
    {
        using var api = new FakeApi(
            new Reply(200, Bodies.Envelope(@"[{""id"":""g1""}]", nextCursor: "c2")),
            new Reply(200, Bodies.Envelope(@"[{""id"":""g2""}]")));

        var seen = new List<string>();
        await foreach (var group in api.Client().Identity.GroupsAsync())
        {
            seen.Add(group.Id);
        }

        Assert.Equal(["g1", "g2"], seen);
    }

    [Fact]
    public async Task ListAddressBooks()
    {
        using var api = new FakeApi(new Reply(200, Bodies.Envelope(
            @"[{""id"":""abk1"",""name"":""Default"",""contact_count"":47}]")));

        var books = await api.Client().Contacts.ListAddressBooksAsync();

        Assert.Equal("GET /v1/address-books", api.Last.Route);
        Assert.Equal(47, books[0].ContactCount);
    }

    [Fact]
    public async Task ListContacts_sends_every_filter()
    {
        using var api = new FakeApi(new Reply(200, Bodies.Envelope(
            @"[{""id"":""k1"",""name"":""Bob Smith"",
                ""emails"":[{""address"":""bob@example.net"",""type"":""work""}]}]")));

        var (contacts, _) = await api.Client().Contacts.ListContactsAsync(new ListContactsQuery
        {
            AddressBookId = "abk1",
            Q = "bob",
            Limit = 25,
            Cursor = "c2",
        });

        Assert.Equal("GET /v1/contacts", api.Last.Route);
        Assert.Equal(["abk1"], api.Last.Query["address_book_id"]);
        Assert.Equal(["bob"], api.Last.Query["q"]);
        Assert.Equal("bob@example.net", contacts[0].Emails![0].Address);
    }

    [Fact]
    public async Task Contacts_walks_the_pages()
    {
        using var api = new FakeApi(
            new Reply(200, Bodies.Envelope(@"[{""id"":""k1""}]", nextCursor: "c2")),
            new Reply(200, Bodies.Envelope(@"[{""id"":""k2""}]")));

        var seen = new List<string>();
        await foreach (var contact in api.Client().Contacts.ContactsAsync(new ListContactsQuery { Q = "b" }))
        {
            seen.Add(contact.Id!);
        }

        Assert.Equal(["k1", "k2"], seen);
    }

    [Fact]
    public async Task Contact_write_and_delete_map_to_their_routes()
    {
        using var api = new FakeApi(
            new Reply(200, Bodies.Envelope(@"{""id"":""k1"",""name"":""Bob""}")),
            new Reply(200, Bodies.Envelope(@"{""id"":""k1"",""title"":""Buyer""}")),
            new Reply(204, ""));

        var client = api.Client();
        await client.Contacts.GetContactAsync("k1");
        await client.Contacts.CreateContactAsync(new Contact { Name = "Bob" });
        await client.Contacts.DeleteContactAsync("k1");

        Assert.Equal(
            ["GET /v1/contacts/k1", "POST /v1/contacts", "DELETE /v1/contacts/k1"],
            api.Requests.Select(r => r.Route).ToArray());
    }

    [Fact]
    public async Task UpdateContact_sends_only_what_changed()
    {
        using var api = new FakeApi(new Reply(200, Bodies.Envelope(@"{""id"":""k1""}")));

        await api.Client().Contacts.UpdateContactAsync("k1", new Contact { Title = "Buyer" });

        Assert.Equal("PATCH /v1/contacts/k1", api.Last.Route);
        Assert.Contains("Buyer", api.Last.Body);
        Assert.DoesNotContain("\"name\"", api.Last.Body);
    }
}
