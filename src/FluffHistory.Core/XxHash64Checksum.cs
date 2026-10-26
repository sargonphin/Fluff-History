namespace FluffHistory.Core;

/// <summary>A seed-zero xxHash64 accidental-corruption checksum, encoded as 16 hexadecimal digits.</summary>
public sealed record XxHash64Checksum
{
    public const string Algorithm = "xxHash64";

    public XxHash64Checksum(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length != 16 || !value.All(char.IsAsciiHexDigit))
        {
            throw new ArgumentException("An xxHash64 value must contain exactly 16 hexadecimal digits.", nameof(value));
        }

        Value = value.ToUpperInvariant();
    }

    public string Value { get; }
}
