using System.Text.RegularExpressions;

namespace SoftcomSmartProvisioner.SelfHostBridge;

public static partial class SecretSanitizer
{
    public static string Clean(string? value)
    {
        var text = value ?? "Falha não detalhada.";
        text = Bearer().Replace(text, "Bearer <oculto>");
        text = SensitiveAssignment().Replace(text, "$1=<oculto>");
        return text.Length <= 800 ? text : text[..800];
    }

    [GeneratedRegex("(?i)(client_?secret|relayclientsecret|access_?token|refresh_?token|authorization|cookie|password|senha|sqlcipher(?:_key)?|secret_?id)[\\\"']?\\s*[:=]\\s*[\\\"']?[^\\\"',;\\s}\\]]+")]
    private static partial Regex SensitiveAssignment();

    [GeneratedRegex("(?i)Bearer\\s+[^,;\\s]+")]
    private static partial Regex Bearer();
}

public sealed class BridgeValidationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
