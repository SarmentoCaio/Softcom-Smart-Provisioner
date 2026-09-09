using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SoftcomSmartProvisioner.Services;

internal static class EmbeddedAppSecrets
{
    private static readonly string[] KeyParts =
    {
        "23e982a36f5e8c2d",
        "347743393b27cb23",
        "2896b2262c964568",
        "fab80d250d1f720c"
    };

    private const string Token = "RyQpSIj2BnLMu5mC_OabSMw-8PsTZMfTI7hujnk7eQkKJvA67jd540MGTf2VQPgInmvsa6YKAV3jhv1n4uWs54HUCclFK_EdrtouBN19TNKP7pBPck78acuyIeiQaTskTkGUtfdiPgcIy_-T0NapZ1gSUPhASxcptDo0UuDqA3EJ4T_g90CMkqa9TgyxiY81ERBsAkP-p5Es10U9-4RjA9gd4Qcdqs50YhPF6Qt6ntIPw5jWp7UcWvTHeAW1gdDc55YCzXwEzlikpa5-gqVeiHQeXI_fh18z3CoyraCWdayd_ZN_MAGKC4WnSrEpAt_JnWC8Ic2V2f-lhMT329C4ZUf9a3CVI1ywoMWwRwb67t8PnF2AOxf_6r44KD11HjdkaNKjpZeZjfb58PSo9SCsKgc=";
    private static readonly byte[] Aad = Encoding.UTF8.GetBytes("Softcom.SmartProvisioner.Secrets.v1");
    private static readonly Lazy<IReadOnlyDictionary<string, string>> Values = new(Decrypt);

    public static string Get(string key) =>
        Values.Value.TryGetValue(key, out var value) ? value : string.Empty;

    private static IReadOnlyDictionary<string, string> Decrypt()
    {
        try
        {
            var key = Convert.FromHexString(string.Concat(KeyParts));
            var raw = Convert.FromBase64String(Token.Replace('-', '+').Replace('_', '/') + new string('=', (4 - Token.Length % 4) % 4));
            var nonce = raw[..12];
            var ciphertextWithTag = raw[12..];
            if (ciphertextWithTag.Length < 16) return new Dictionary<string, string>();

            var ciphertext = ciphertextWithTag[..^16];
            var tag = ciphertextWithTag[^16..];
            var plaintext = new byte[ciphertext.Length];
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, Aad);

            return JsonSerializer.Deserialize<Dictionary<string, string>>(Encoding.UTF8.GetString(plaintext))
                ?? new Dictionary<string, string>();
        }
        catch
        {
            return new Dictionary<string, string>();
        }
    }
}
