using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace SoftcomSmartProvisioner.Services;

/// <summary>
/// Lê a configuração persistida pelo SelfHost 4.1 usando o mesmo SQLCipher distribuído
/// com a instalação. A chave não é mantida no Provisioner: ela é obtida do campo constante
/// ConfigPaths.DatabasePassword nos metadados do SelfHost.API.dll.
/// </summary>
internal static class SelfHostModernConfigReader
{
    private const int SqliteOpenReadOnly = 0x00000001;
    private const int SqliteRow = 100;
    private const int SqliteDone = 101;

    public static SelfHostDeviceService.SelfHostRootConfig Load(string installPath)
    {
        var assemblyPath = Path.Combine(installPath, "SelfHost.API.dll");
        var databasePath = Path.Combine(installPath, "data", "selfhost-config.db");
        var nativePath = ResolveSqlCipherPath(installPath);

        if (!File.Exists(assemblyPath))
            throw new InvalidOperationException("SelfHost 4.1 está instalado, mas SelfHost.API.dll não foi localizado.");
        if (!File.Exists(databasePath))
            throw new InvalidOperationException("SelfHost 4.1 está instalado, mas data\\selfhost-config.db não foi localizado.");
        if (nativePath is null)
            throw new InvalidOperationException("A biblioteca SQLCipher da instalação do SelfHost 4.1 não foi localizada para esta arquitetura.");

        var databasePassword = ReadDatabasePassword(assemblyPath);
        string payload;
        try
        {
            payload = ReadPayload(nativePath, databasePath, databasePassword);
        }
        finally
        {
            databasePassword = string.Empty;
        }

        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            var baseUrl = FindValue(root, "SoftcomShopUrlBase", "SoftcomShopUrl").TrimEnd('/');
            var clientId = FindValue(root, "SoftcomShopClientId");
            var clientSecret = FindValue(root, "SoftcomShopSecretId");
            var company = FindValue(root, "SoftcomShopEmpresa", "EmpresaNome", "RazaoSocial");
            var companyCnpj = FindValue(root, "SoftcomShopEmpresaCnpj", "EmpresaCnpj", "CNPJ");
            var portText = FindValue(root, "PortaHTTP");
            var port = int.TryParse(portText, out var configuredPort) && configuredPort is > 0 and <= 65535
                ? configuredPort
                : 7711;

            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                throw new InvalidOperationException("A URL do Softcomshop no selfhost-config.db é inválida.");
            }

            if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
            {
                throw new InvalidOperationException(
                    "O selfhost-config.db não contém as credenciais raiz do Softcomshop necessárias para administrar dispositivos.");
            }

            return new SelfHostDeviceService.SelfHostRootConfig(
                baseUrl, clientId, clientSecret, company, companyCnpj, port);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("A configuração armazenada no selfhost-config.db não contém JSON válido.");
        }
        finally
        {
            payload = string.Empty;
        }
    }

    private static string? ResolveSqlCipherPath(string installPath)
    {
        var runtime = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X86 => "win-x86",
            Architecture.X64 => "win-x64",
            Architecture.Arm => "win-arm",
            Architecture.Arm64 => "win-arm64",
            _ => string.Empty
        };

        if (string.IsNullOrWhiteSpace(runtime)) return null;
        var path = Path.Combine(installPath, "runtimes", runtime, "native", "e_sqlcipher.dll");
        return File.Exists(path) ? path : null;
    }

    private static string ReadDatabasePassword(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);
        if (!pe.HasMetadata)
            throw new InvalidOperationException("SelfHost.API.dll não contém metadados .NET reconhecidos.");

        var metadata = pe.GetMetadataReader();
        foreach (var typeHandle in metadata.TypeDefinitions)
        {
            var type = metadata.GetTypeDefinition(typeHandle);
            if (!metadata.GetString(type.Name).Equals("ConfigPaths", StringComparison.Ordinal) ||
                !metadata.GetString(type.Namespace).Equals("Softcom.Integracao.API.Util", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var fieldHandle in type.GetFields())
            {
                var field = metadata.GetFieldDefinition(fieldHandle);
                if (!metadata.GetString(field.Name).Equals("DatabasePassword", StringComparison.Ordinal)) continue;
                var constantHandle = field.GetDefaultValue();
                if (constantHandle.IsNil) break;

                var constant = metadata.GetConstant(constantHandle);
                if (constant.TypeCode != ConstantTypeCode.String) break;
                var bytes = metadata.GetBlobBytes(constant.Value);
                var value = Encoding.Unicode.GetString(bytes).TrimEnd('\0');
                Array.Clear(bytes);
                if (!string.IsNullOrWhiteSpace(value)) return value;
                break;
            }
        }

        throw new InvalidOperationException(
            "Não foi possível obter com segurança a chave SQLCipher usada pelo SelfHost 4.1 a partir de ConfigPaths.DatabasePassword.");
    }

    private static string ReadPayload(string nativePath, string databasePath, string password)
    {
        using var sqlite = new SqlCipherNative(nativePath);
        IntPtr db = IntPtr.Zero;
        IntPtr statement = IntPtr.Zero;
        byte[]? passwordBytes = null;
        try
        {
            if (sqlite.Open(databasePath, out db, SqliteOpenReadOnly, IntPtr.Zero) != 0 || db == IntPtr.Zero)
                throw new InvalidOperationException("Não foi possível abrir o selfhost-config.db em modo somente leitura.");

            passwordBytes = Encoding.UTF8.GetBytes(password);
            if (sqlite.Key(db, passwordBytes, passwordBytes.Length) != 0)
                throw new InvalidOperationException("O SQLCipher do SelfHost não aceitou a chave de configuração instalada.");

            const string sql = "SELECT payload_json FROM configuracoes WHERE id = 1 LIMIT 1;";
            if (sqlite.Prepare(db, sql, -1, out statement, IntPtr.Zero) != 0 || statement == IntPtr.Zero)
                throw new InvalidOperationException("Não foi possível consultar a configuração do SelfHost 4.1.");

            var step = sqlite.Step(statement);
            if (step == SqliteDone)
                throw new InvalidOperationException("O selfhost-config.db ainda não possui uma configuração ativa.");
            if (step != SqliteRow)
                throw new InvalidOperationException("Falha ao ler a configuração do SelfHost 4.1.");

            var pointer = sqlite.ColumnText(statement, 0);
            var length = sqlite.ColumnBytes(statement, 0);
            if (pointer == IntPtr.Zero || length <= 0)
                throw new InvalidOperationException("A configuração ativa do SelfHost 4.1 está vazia.");

            var bytes = new byte[length];
            Marshal.Copy(pointer, bytes, 0, length);
            try
            {
                return Encoding.UTF8.GetString(bytes);
            }
            finally
            {
                Array.Clear(bytes);
            }
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch
        {
            throw new InvalidOperationException("Não foi possível ler com segurança o selfhost-config.db do SelfHost 4.1.");
        }
        finally
        {
            if (passwordBytes is not null) Array.Clear(passwordBytes);
            if (statement != IntPtr.Zero) sqlite.Finalize(statement);
            if (db != IntPtr.Zero) sqlite.Close(db);
        }
    }

    private static string FindValue(JsonElement root, params string[] names)
    {
        // A ordem dos nomes é prioridade semântica (por exemplo, UrlBase antes de Url),
        // independentemente da ordem em que as propriedades foram serializadas no banco.
        foreach (var name in names)
        {
            foreach (var value in FindValues(root, new[] { name }, 0))
            {
                if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
            }
        }
        return string.Empty;
    }

    private static IEnumerable<string> FindValues(JsonElement element, string[] names, int depth)
    {
        if (depth > 12) yield break;
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (names.Any(name => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                {
                    if (property.Value.ValueKind == JsonValueKind.String)
                        yield return property.Value.GetString() ?? string.Empty;
                    else if (property.Value.ValueKind == JsonValueKind.Number)
                        yield return property.Value.ToString();
                }

                foreach (var nested in FindValues(property.Value, names, depth + 1)) yield return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                foreach (var nested in FindValues(item, names, depth + 1)) yield return nested;
        }
    }

    private sealed class SqlCipherNative : IDisposable
    {
        private readonly IntPtr _library;
        private readonly SqliteOpen _open;
        private readonly SqliteKey _key;
        private readonly SqlitePrepare _prepare;
        private readonly SqliteStep _step;
        private readonly SqliteColumnText _columnText;
        private readonly SqliteColumnBytes _columnBytes;
        private readonly SqliteFinalize _finalize;
        private readonly SqliteClose _close;

        public SqlCipherNative(string path)
        {
            try
            {
                _library = NativeLibrary.Load(path);
                _open = Load<SqliteOpen>("sqlite3_open_v2");
                _key = Load<SqliteKey>("sqlite3_key");
                _prepare = Load<SqlitePrepare>("sqlite3_prepare_v2");
                _step = Load<SqliteStep>("sqlite3_step");
                _columnText = Load<SqliteColumnText>("sqlite3_column_text");
                _columnBytes = Load<SqliteColumnBytes>("sqlite3_column_bytes");
                _finalize = Load<SqliteFinalize>("sqlite3_finalize");
                _close = Load<SqliteClose>("sqlite3_close_v2");
            }
            catch
            {
                if (_library != IntPtr.Zero) NativeLibrary.Free(_library);
                throw new InvalidOperationException("Não foi possível carregar o SQLCipher distribuído com o SelfHost 4.1.");
            }
        }

        public int Open(string fileName, out IntPtr db, int flags, IntPtr vfs) => _open(fileName, out db, flags, vfs);
        public int Key(IntPtr db, byte[] key, int length) => _key(db, key, length);
        public int Prepare(IntPtr db, string sql, int length, out IntPtr statement, IntPtr tail) => _prepare(db, sql, length, out statement, tail);
        public int Step(IntPtr statement) => _step(statement);
        public IntPtr ColumnText(IntPtr statement, int column) => _columnText(statement, column);
        public int ColumnBytes(IntPtr statement, int column) => _columnBytes(statement, column);
        public int Finalize(IntPtr statement) => _finalize(statement);
        public int Close(IntPtr db) => _close(db);

        private T Load<T>(string export) where T : Delegate =>
            Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_library, export));

        public void Dispose()
        {
            if (_library != IntPtr.Zero) NativeLibrary.Free(_library);
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int SqliteOpen([MarshalAs(UnmanagedType.LPUTF8Str)] string fileName, out IntPtr db, int flags, IntPtr vfs);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int SqliteKey(IntPtr db, byte[] key, int keyLength);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int SqlitePrepare(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int byteCount, out IntPtr statement, IntPtr tail);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int SqliteStep(IntPtr statement);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr SqliteColumnText(IntPtr statement, int column);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int SqliteColumnBytes(IntPtr statement, int column);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int SqliteFinalize(IntPtr statement);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int SqliteClose(IntPtr db);
    }
}
