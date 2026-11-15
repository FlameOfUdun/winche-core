using System.Text.Json;
using System.Text.Json.Nodes;

namespace Winche.Core;

/// <summary>Where an area's values come from right now.</summary>
public enum SettingsSource
{
    /// <summary>The values the app's code registered: nothing saved, or reset since.</summary>
    Code,
    /// <summary>A version saved in the store (by the console, the CLI, or your own tools).</summary>
    Saved,
}

/// <summary>
/// One group of settings the console can change — "auth.identityProviders", "database.indexes" — as one JSON document
/// of type <typeparamref name="T"/>. The app's code supplies the starting values; a saved version replaces them on every
/// instance within seconds; resetting goes back to the code's. Register with <c>AddWincheSettingsArea</c>, read
/// <see cref="Current"/>, and subscribe to <see cref="Changed"/>.
/// </summary>
public sealed class SettingsArea<T> : SettingsArea where T : class
{
    private readonly Func<T, IReadOnlyList<string>> _validate;
    private volatile T _current;

    internal SettingsArea(string name, string title, T codeDefault, Func<T, IReadOnlyList<string>>? validate)
        : base(name, title)
    {
        CodeDefault = codeDefault;
        _current = codeDefault;
        _validate = validate ?? (_ => []);
    }

    /// <summary>The values from the app's code.</summary>
    public T CodeDefault { get; }

    /// <summary>The values in force: the saved version, or the code's.</summary>
    public T Current => _current;

    /// <summary>Raised (on a background thread) after <see cref="Current"/> changes.</summary>
    public event Action<T>? Changed;

    public override Type DocumentType => typeof(T);

    public override JsonNode CodeDefaultJson => JsonSerializer.SerializeToNode(CodeDefault, Json)!;

    public override JsonNode CurrentJson => JsonSerializer.SerializeToNode(_current, Json)!;

    public override IReadOnlyList<string> Validate(JsonNode document)
    {
        T? value;
        try { value = document.Deserialize<T>(Json); }
        catch (JsonException ex) { return [$"The settings don't have the expected shape: {ex.Message}"]; }
        return value is null ? ["The settings are empty."] : _validate(value);
    }

    internal override void Apply(SettingsVersion? saved)
    {
        var next = saved?.Document is { } doc ? doc.Deserialize<T>(Json) ?? CodeDefault : CodeDefault;
        Version = saved;
        _current = next;
        Changed?.Invoke(next);
    }
}

/// <summary>An area, untyped: what the store and the console work with.</summary>
public abstract class SettingsArea(string name, string title)
{
    /// <summary>JSON as the store keeps it: camelCase, enums as strings.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    /// <summary>The area's key, e.g. <c>auth.identityProviders</c>.</summary>
    public string Name { get; } = name;

    /// <summary>A name for people, e.g. "Identity providers".</summary>
    public string Title { get; } = title;

    /// <summary>The saved version in force, or null when the code's values apply.</summary>
    public SettingsVersion? Version { get; protected set; }

    public SettingsSource Source => Version?.Document is null ? SettingsSource.Code : SettingsSource.Saved;

    public abstract Type DocumentType { get; }
    public abstract JsonNode CodeDefaultJson { get; }
    public abstract JsonNode CurrentJson { get; }

    /// <summary>Problems with a document before it's saved; empty when it's fine.</summary>
    public abstract IReadOnlyList<string> Validate(JsonNode document);

    internal abstract void Apply(SettingsVersion? saved);
}

/// <summary>One saved change to an area. A null <see cref="Document"/> is a reset to the code's values.</summary>
public sealed record SettingsVersion(
    long Id, string Area, int Number, JsonNode? Document, DateTimeOffset At, string? By, string? Reason, long? RestoredFrom);

/// <summary>A document that failed its area's checks.</summary>
public sealed class SettingsValidationException(string area, IReadOnlyList<string> errors)
    : Exception($"The {area} settings can't be saved: {string.Join(" ", errors)}")
{
    public string Area { get; } = area;
    public IReadOnlyList<string> Errors { get; } = errors;
}
