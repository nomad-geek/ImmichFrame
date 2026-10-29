using System.Text.Json.Serialization;

namespace ImmichFrame.Core.Models;

/// <summary>How an account's assets are ordered.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ShuffleMode
{
    /// <summary>Each batch is an independent random pick, so assets can repeat.</summary>
    Random = 0,

    /// <summary>Every matching asset is shown once, in random order, before any repeats; each client keeps its own order.</summary>
    Exhaustive = 1
}
