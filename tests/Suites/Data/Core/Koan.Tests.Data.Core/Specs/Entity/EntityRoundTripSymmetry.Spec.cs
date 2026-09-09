using AwesomeAssertions;
using Koan.Data.Core.Polymorphism;
using Koan.Data.Core.Model;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Koan.Tests.Data.Core.Specs.Entity;

/// <summary>
/// What Koan writes, Koan reads back. The asymmetric case is state an Entity guards behind a non-public setter:
/// Json.NET serializes it happily and then refuses to fill it, so the value reaches storage and is silently
/// replaced by a default on the way back — a write that reported success and a read that quietly lost it.
///
/// <para>This is not hypothetical. The Canon pillar keeps aggregation ownership that way, so a second arrival for
/// an already-known key read back an index with no owner, minted a fresh canonical record, and the pillar's whole
/// promise — messy arrivals converge — failed on the two-arrival case it exists for.</para>
/// </summary>
public sealed class EntityRoundTripSymmetrySpec
{
    [Fact]
    public void A_seeded_additive_collection_replaces_its_constructor_values()
    {
        var source = new SeededBucketOwner { Id = "seeded-owner" };
        source.Values.Add("saved");
        var json = EntityJsonSerialization.SerializeDocument(source);
        var restored = (SeededBucketOwner)EntityJsonSerialization.DeserializeDocument(json, typeof(SeededBucketOwner));
        restored.Values.Should().Equal("seed", "saved");
    }

    private sealed class SeededBucketOwner : Entity<SeededBucketOwner>
    {
        public SeededBucket Values { get; set; } = new();
    }
    public sealed class SeededBucket : IEnumerable<string>
    {
        private readonly List<string> _values = ["seed"];
        public void Add(string value) => _values.Add(value);
        public void Clear() => _values.Clear();
        public IEnumerator<string> GetEnumerator() => _values.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Fact]
    public void Enum_names_are_strings_in_documents_and_query_comparands()
    {
        var source = new EnumOwner
        {
            Id = "enum-owner", State = State.Published, Optional = State.Suppressed,
            States = [State.Published, State.Suppressed], Permissions = Permission.Read | Permission.Write,
            Alias = AliasState.Ready
        };
        var json = EntityJsonSerialization.SerializeDocument(source);
        var document = JObject.Parse(json);
        document["state"]!.Value<string>().Should().Be("Published");
        document["state"]!.Type.Should().Be(JTokenType.String);
        document["optional"]!.Value<string>().Should().Be("Suppressed");
        document["states"]!.Values<string>().Should().Equal("Published", "Suppressed");
        document["permissions"]!.Value<string>().Should().Be("Read, Write");
        document["alias"]!.Value<string>().Should().Be("ready-for-use");
        Koan.Data.Core.ComparableScalarEncoding.EncodeComparand(State.Published).Should().Be("Published");
        Koan.Data.Core.ComparableScalarEncoding.EncodeComparand(AliasState.Ready).Should().Be("ready-for-use");
        var restored = (EnumOwner)EntityJsonSerialization.DeserializeDocument(json, typeof(EnumOwner));
        restored.State.Should().Be(source.State);
        restored.Optional.Should().Be(source.Optional);
        restored.States.Should().Equal(source.States);
        restored.Permissions.Should().Be(source.Permissions);
        restored.Alias.Should().Be(source.Alias);
    }

    [Fact]
    public void Unnamed_enum_values_cannot_silently_be_written_as_numbers()
    {
        var save = () => EntityJsonSerialization.SerializeDocument(new EnumOwner { State = (State)1234 });
        save.Should().Throw<JsonSerializationException>();
        var compare = () => Koan.Data.Core.ComparableScalarEncoding.EncodeComparand((State)1234);
        compare.Should().Throw<JsonSerializationException>();
    }

    private enum State { Draft, Published, Suppressed }
    [Flags] private enum Permission { None = 0, Read = 1, Write = 2 }
    private enum AliasState { [System.Runtime.Serialization.EnumMember(Value = "ready-for-use")] Ready }
    private sealed class EnumOwner : Entity<EnumOwner>
    {
        public State State { get; set; }
        public State? Optional { get; set; }
        public State[] States { get; set; } = [];
        public Permission Permissions { get; set; }
        public AliasState Alias { get; set; }
    }

    [Fact]
    public void Additive_domain_collection_round_trips_inside_a_dictionary()
    {
        var source = new BucketOwner { Id = "bucket-owner" };
        source.Buckets["game"] = new Bucket();
        source.Buckets["game"].Add("example");
        var json = EntityJsonSerialization.SerializeDocument(source);
        var restored = (BucketOwner)EntityJsonSerialization.DeserializeDocument(json, typeof(BucketOwner));
        restored.Buckets["game"].Should().Equal("example");
        JObject.Parse(json)["buckets"]!["game"].Should().BeOfType<JArray>();
    }

    private sealed class BucketOwner : Entity<BucketOwner>
    {
        public Dictionary<string, Bucket> Buckets { get; set; } = new();
    }

    public sealed class Bucket : IEnumerable<string>
    {
        private readonly List<string> _values = new();
        public void Add(string value) => _values.Add(value);
        public IEnumerator<string> GetEnumerator() => _values.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class Guarded : Entity<Guarded>
    {
        public string Label { get; set; } = "";
        public string Owner { get; private set; } = "";
        public int Revision { get; private set; }
        public string? Note { get; internal set; }

        // The only door domain code may use; persistence must not need it.
        public void Claim(string owner, int revision, string? note)
        {
            Owner = owner;
            Revision = revision;
            Note = note;
        }

        public string Computed => $"{Label}:{Owner}";
    }

    private static readonly JsonSerializerSettings Settings =
        EntityJsonSerialization.Apply(new JsonSerializerSettings());

    [Fact(DisplayName = "state behind a non-public setter survives the round trip")]
    public void Guarded_state_round_trips()
    {
        var entity = new Guarded { Id = "guarded-1", Label = "ledger" };
        entity.Claim("canonical-42", 7, "held");

        var restored = JsonConvert.DeserializeObject<Guarded>(
            JsonConvert.SerializeObject(entity, Settings), Settings)!;

        restored.Owner.Should().Be("canonical-42", "persistence restores what persistence wrote");
        restored.Revision.Should().Be(7);
        restored.Note.Should().Be("held");
        restored.Label.Should().Be("ledger");
        restored.Id.Should().Be("guarded-1");
    }

    [Fact(DisplayName = "a computed property is written for readers and never read back")]
    public void Computed_properties_stay_read_only()
    {
        var entity = new Guarded { Id = "guarded-2", Label = "ledger" };
        entity.Claim("canonical-42", 1, null);

        var document = JObject.Parse(JsonConvert.SerializeObject(entity, Settings));
        document.Property("computed", System.StringComparison.OrdinalIgnoreCase)
            .Should().NotBeNull("a computed property is still useful to whoever reads the document");

        // It has no setter, so there is nothing to restore and nothing to lose: it is derived again on read.
        document["computed"] = "tampered";
        var restored = JsonConvert.DeserializeObject<Guarded>(document.ToString(), Settings)!;
        restored.Computed.Should().Be("ledger:canonical-42");
    }
}
