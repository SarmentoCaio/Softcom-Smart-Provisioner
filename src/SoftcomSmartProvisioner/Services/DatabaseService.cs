using MySqlConnector;
using SoftcomSmartProvisioner.Models;

namespace SoftcomSmartProvisioner.Services;

public sealed class DatabaseService
{
    private readonly SecretStore _secretStore;
    private string? _overrideHost;
    private int? _overridePort;

    public DatabaseService(SecretStore secretStore)
    {
        _secretStore = secretStore;
    }

    public void SetConnectionOverride(string? host, int? port)
    {
        _overrideHost = string.IsNullOrWhiteSpace(host) ? null : host.Trim();
        _overridePort = port is > 0 ? port : null;
    }

    public async Task<IReadOnlyList<string>> GetDatabasesAsync(
        string environmentKey,
        CancellationToken cancellationToken = default)
    {
        var environment = EnvironmentCatalog.Get(environmentKey);
        await using var connection = new MySqlConnection(BuildConnectionString(environment, database: null));
        await connection.OpenAsync(cancellationToken);

        var databases = new List<string>();
        await using var command = connection.CreateCommand();
        command.CommandText = "SHOW DATABASES";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var name = reader.GetString(0);
            if (name.StartsWith(EnvironmentCatalog.DatabasePrefix, StringComparison.OrdinalIgnoreCase))
            {
                databases.Add(name);
            }
        }

        return databases.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<IReadOnlyList<CompanyInfo>> GetCompaniesAsync(
        string environmentKey,
        string database,
        CancellationToken cancellationToken = default)
    {
        var environment = EnvironmentCatalog.Get(environmentKey);
        await using var connection = new MySqlConnection(BuildConnectionString(environment, database));
        await connection.OpenAsync(cancellationToken);

        var columns = await GetColumnsAsync(connection, "empresa", cancellationToken);
        if (!columns.Contains("id"))
        {
            throw new InvalidOperationException("A tabela empresa existe, mas o campo id nao foi localizado.");
        }

        var nameColumn = Pick(columns, "nome", "nome_fantasia", "fantasia", "razao_social", "descricao");
        var cnpjColumn = Pick(columns, "cnpj", "cpf_cnpj", "documento");

        var selected = new List<string> { "`id`" };
        if (nameColumn is not null) selected.Add($"`{nameColumn}`");
        if (cnpjColumn is not null && !string.Equals(cnpjColumn, nameColumn, StringComparison.OrdinalIgnoreCase))
            selected.Add($"`{cnpjColumn}`");

        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {string.Join(", ", selected)} FROM `empresa` ORDER BY `id` LIMIT 500";

        var result = new List<CompanyInfo>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = Convert.ToInt64(reader["id"]);
            var name = nameColumn is null ? $"Empresa {id}" : Convert.ToString(reader[nameColumn])?.Trim();
            var cnpj = cnpjColumn is null ? string.Empty : Convert.ToString(reader[cnpjColumn])?.Trim();

            result.Add(new CompanyInfo(
                id,
                string.IsNullOrWhiteSpace(name) ? $"Empresa {id}" : name!,
                cnpj ?? string.Empty));
        }

        return result;
    }

    public async Task<IReadOnlyList<OAuthClientInfo>> GetOAuthClientsAsync(
        string environmentKey,
        string database,
        long companyId,
        CancellationToken cancellationToken = default)
    {
        var environment = EnvironmentCatalog.Get(environmentKey);
        await using var connection = new MySqlConnection(BuildConnectionString(environment, database));
        await connection.OpenAsync(cancellationToken);

        var columns = await GetColumnsAsync(connection, "oauth_clients", cancellationToken);
        var required = new[] { "client_id", "name" };
        foreach (var column in required)
        {
            if (!columns.Contains(column))
            {
                throw new InvalidOperationException($"O campo {column} nao foi localizado em oauth_clients.");
            }
        }

        var wanted = new[]
        {
            "client_id", "name", "device_id", "empresa_id", "created_at",
            "updated_at", "deleted_at", "visivel", "previous_device_id", "tipo_dispositivo"
        };
        var selected = wanted.Where(columns.Contains).Select(x => $"`{x}`").ToArray();

        var where = new List<string>();
        if (columns.Contains("empresa_id"))
        {
            where.Add("`empresa_id` = @empresaId");
        }

        if (columns.Contains("deleted_at"))
        {
            where.Add("`deleted_at` IS NULL");
        }

        if (columns.Contains("visivel"))
        {
            where.Add("COALESCE(`visivel`, 1) = 1");
        }

        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT {string.Join(", ", selected)} FROM `oauth_clients`" +
            (where.Count > 0 ? $" WHERE {string.Join(" AND ", where)}" : string.Empty) +
            " ORDER BY `name`";

        if (columns.Contains("empresa_id"))
        {
            command.Parameters.AddWithValue("@empresaId", companyId);
        }

        var result = new List<OAuthClientInfo>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new OAuthClientInfo(
                Read(reader, "client_id"),
                Read(reader, "name"),
                Read(reader, "device_id"),
                ReadNullableLong(reader, "empresa_id"),
                ReadNullable(reader, "created_at"),
                ReadNullable(reader, "updated_at"),
                ReadNullable(reader, "previous_device_id"),
                ReadNullable(reader, "tipo_dispositivo")));
        }

        return result;
    }

    public async Task<OAuthClientInfo?> GetOAuthClientAsync(
        string environmentKey,
        string database,
        long companyId,
        string clientId,
        CancellationToken cancellationToken = default)
    {
        var clients = await GetOAuthClientsAsync(environmentKey, database, companyId, cancellationToken);
        return clients.FirstOrDefault(x => string.Equals(x.ClientId, clientId, StringComparison.Ordinal));
    }

    public async Task<IReadOnlyList<OAuthClientInfo>> GetOAuthClientsByDeviceIdAsync(
        string environmentKey,
        string database,
        long companyId,
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return Array.Empty<OAuthClientInfo>();
        }

        var environment = EnvironmentCatalog.Get(environmentKey);
        await using var connection = new MySqlConnection(BuildConnectionString(environment, database));
        await connection.OpenAsync(cancellationToken);

        var columns = await GetColumnsAsync(connection, "oauth_clients", cancellationToken);
        if (!columns.Contains("client_id") || !columns.Contains("name") || !columns.Contains("device_id"))
        {
            throw new InvalidOperationException(
                "Nao foi possivel pesquisar o Device ID: campos obrigatorios nao foram localizados em oauth_clients.");
        }

        var wanted = new[]
        {
            "client_id", "name", "device_id", "empresa_id", "created_at",
            "updated_at", "deleted_at", "visivel", "previous_device_id", "tipo_dispositivo"
        };
        var selected = wanted.Where(columns.Contains).Select(x => $"`{x}`").ToArray();

        var where = new List<string> { "`device_id` = @deviceId" };
        if (columns.Contains("empresa_id"))
        {
            where.Add("`empresa_id` = @empresaId");
        }
        if (columns.Contains("deleted_at"))
        {
            where.Add("`deleted_at` IS NULL");
        }

        // Nao filtra por visivel: um cadastro oculto ainda pode manter o Device ID e bloquear o Smart.
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT {string.Join(", ", selected)} FROM `oauth_clients` WHERE {string.Join(" AND ", where)} ORDER BY `name`";
        command.Parameters.AddWithValue("@deviceId", deviceId.Trim());
        if (columns.Contains("empresa_id"))
        {
            command.Parameters.AddWithValue("@empresaId", companyId);
        }

        var result = new List<OAuthClientInfo>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new OAuthClientInfo(
                Read(reader, "client_id"),
                Read(reader, "name"),
                Read(reader, "device_id"),
                ReadNullableLong(reader, "empresa_id"),
                ReadNullable(reader, "created_at"),
                ReadNullable(reader, "updated_at"),
                ReadNullable(reader, "previous_device_id"),
                ReadNullable(reader, "tipo_dispositivo")));
        }

        return result;
    }

    public async Task<bool> UnlinkOAuthClientAsync(
        string environmentKey,
        string database,
        long companyId,
        string clientId,
        CancellationToken cancellationToken = default)
    {
        var environment = EnvironmentCatalog.Get(environmentKey);
        await using var connection = new MySqlConnection(BuildConnectionString(environment, database));
        await connection.OpenAsync(cancellationToken);

        var columns = await GetColumnsAsync(connection, "oauth_clients", cancellationToken);
        if (!columns.Contains("client_id") || !columns.Contains("device_id"))
        {
            throw new InvalidOperationException(
                "Nao foi possivel desvincular o dispositivo: client_id/device_id nao foram localizados em oauth_clients.");
        }

        var assignments = new List<string>();
        if (columns.Contains("previous_device_id"))
        {
            assignments.Add("`previous_device_id` = CASE WHEN COALESCE(`device_id`, '') <> '' THEN `device_id` ELSE `previous_device_id` END");
        }

        // Os cadastros disponiveis observados no Softcomshop utilizam string vazia no device_id.
        assignments.Add("`device_id` = ''");

        if (columns.Contains("updated_at"))
        {
            assignments.Add("`updated_at` = NOW()");
        }

        var where = new List<string> { "`client_id` = @clientId" };
        if (columns.Contains("empresa_id"))
        {
            where.Add("`empresa_id` = @empresaId");
        }
        if (columns.Contains("deleted_at"))
        {
            where.Add("`deleted_at` IS NULL");
        }

        await using var command = connection.CreateCommand();
        command.CommandText =
            $"UPDATE `oauth_clients` SET {string.Join(", ", assignments)} WHERE {string.Join(" AND ", where)} LIMIT 1";
        command.Parameters.AddWithValue("@clientId", clientId);
        if (columns.Contains("empresa_id"))
        {
            command.Parameters.AddWithValue("@empresaId", companyId);
        }

        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        return affected > 0;
    }


    public async Task<OAuthClientInfo> CreateOAuthClientAsync(
        string environmentKey,
        string database,
        long companyId,
        string name,
        CancellationToken cancellationToken = default)
    {
        name = (name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException("Informe o nome do novo dispositivo.");
        }
        if (name.Length > 255)
        {
            throw new InvalidOperationException("O nome do dispositivo deve possuir no maximo 255 caracteres.");
        }

        var environment = EnvironmentCatalog.Get(environmentKey);
        await using var connection = new MySqlConnection(BuildConnectionString(environment, database));
        await connection.OpenAsync(cancellationToken);

        var columns = await GetColumnsAsync(connection, "oauth_clients", cancellationToken);
        foreach (var required in new[] { "client_id", "client_secret", "name", "device_id", "empresa_id" })
        {
            if (!columns.Contains(required))
            {
                throw new InvalidOperationException($"Nao foi possivel criar o dispositivo: o campo {required} nao existe em oauth_clients.");
            }
        }

        // Os cadastros atuais usam client_id com 40 caracteres e client_secret com 60,
        // usando o mesmo alfabeto URL-safe observado em oauth_clients.
        var clientId = GenerateOAuthCredential(40);
        var clientSecret = GenerateOAuthCredential(60);

        var insertColumns = new List<string> { "client_id", "client_secret", "name", "device_id", "empresa_id" };
        var insertValues = new List<string> { "@clientId", "@clientSecret", "@name", "''", "@empresaId" };
        if (columns.Contains("visivel"))
        {
            insertColumns.Add("visivel");
            insertValues.Add("1");
        }
        if (columns.Contains("created_at"))
        {
            insertColumns.Add("created_at");
            insertValues.Add("NOW()");
        }
        if (columns.Contains("updated_at"))
        {
            insertColumns.Add("updated_at");
            insertValues.Add("NOW()");
        }

        await using var command = connection.CreateCommand();
        command.CommandText =
            $"INSERT INTO `oauth_clients` ({string.Join(", ", insertColumns.Select(x => $"`{x}`"))}) " +
            $"VALUES ({string.Join(", ", insertValues)})";
        command.Parameters.AddWithValue("@clientId", clientId);
        command.Parameters.AddWithValue("@clientSecret", clientSecret);
        command.Parameters.AddWithValue("@name", name);
        command.Parameters.AddWithValue("@empresaId", companyId);
        await command.ExecuteNonQueryAsync(cancellationToken);

        return new OAuthClientInfo(
            clientId,
            name,
            string.Empty,
            companyId,
            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            null,
            null,
            null);
    }

    public async Task<IReadOnlyList<FiscalSeriesInfo>> GetFiscalSeriesAsync(
        string environmentKey,
        string database,
        long companyId,
        string oauthClientId,
        CancellationToken cancellationToken = default)
    {
        oauthClientId = (oauthClientId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(oauthClientId))
        {
            throw new InvalidOperationException("Selecione um dispositivo Softcomshop para consultar as series.");
        }

        var environment = EnvironmentCatalog.Get(environmentKey);
        await using var connection = new MySqlConnection(BuildConnectionString(environment, database));
        await connection.OpenAsync(cancellationToken);

        var result = new List<FiscalSeriesInfo>();
        await LoadFiscalSeriesTableAsync(connection, "nfce_serie", "NFCe", companyId, oauthClientId, result, cancellationToken);
        await LoadFiscalSeriesTableAsync(connection, "nfe_serie", "NFe", companyId, oauthClientId, result, cancellationToken);
        return result.OrderBy(x => x.DocumentType).ThenBy(x => x.Id).ToArray();
    }

    public async Task<FiscalSeriesInfo> SaveFiscalSeriesAsync(
        string environmentKey,
        string database,
        long companyId,
        string oauthClientId,
        string documentType,
        long? id,
        string series,
        int initialNumber,
        int environmentValue,
        CancellationToken cancellationToken = default)
    {
        oauthClientId = (oauthClientId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(oauthClientId))
        {
            throw new InvalidOperationException("Selecione um dispositivo Softcomshop para salvar a serie.");
        }

        var normalizedType = (documentType ?? string.Empty).Trim().ToLowerInvariant();
        var table = normalizedType switch
        {
            "nfce" => "nfce_serie",
            "nfe" => "nfe_serie",
            _ => throw new InvalidOperationException("Tipo de serie invalido. Use NFCe ou NFe.")
        };
        var displayType = normalizedType == "nfce" ? "NFCe" : "NFe";

        series = (series ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(series))
        {
            throw new InvalidOperationException($"Informe a serie de {displayType}.");
        }
        if (normalizedType == "nfce" && !int.TryParse(series, out _))
        {
            throw new InvalidOperationException("A serie de NFCe precisa ser numerica.");
        }
        if (initialNumber < 1)
        {
            throw new InvalidOperationException("O numero inicial/proximo numero deve ser maior que zero.");
        }
        if (environmentValue is not (1 or 2))
        {
            throw new InvalidOperationException("Ambiente invalido. Use 1 para Producao ou 2 para Homologacao.");
        }

        var environment = EnvironmentCatalog.Get(environmentKey);
        await using var connection = new MySqlConnection(BuildConnectionString(environment, database));
        await connection.OpenAsync(cancellationToken);

        var columns = await GetColumnsAsync(connection, table, cancellationToken);
        foreach (var required in new[] { "id", "empresa_id", "serie", "numeracao_inicial" })
        {
            if (!columns.Contains(required))
            {
                throw new InvalidOperationException($"A tabela {table} nao possui o campo obrigatorio {required}.");
            }
        }
        if (!columns.Contains("oauth_client_id"))
        {
            throw new InvalidOperationException($"A tabela {table} nao possui oauth_client_id; nao e seguro associar a serie ao dispositivo.");
        }

        long savedId;
        if (id.HasValue && id.Value > 0)
        {
            var assignments = new List<string>
            {
                "`serie` = @serie",
                "`numeracao_inicial` = @numero"
            };
            if (columns.Contains("ambiente")) assignments.Add("`ambiente` = @ambiente");
            if (columns.Contains("tipo_serie")) assignments.Add("`tipo_serie` = 'DISPOSITIVO'");
            assignments.Add("`oauth_client_id` = @clientId");
            if (columns.Contains("updated_at")) assignments.Add("`updated_at` = NOW()");
            if (columns.Contains("deleted_at")) assignments.Add("`deleted_at` = NULL");

            await using var update = connection.CreateCommand();
            update.CommandText =
                $"UPDATE `{table}` SET {string.Join(", ", assignments)} " +
                "WHERE `id` = @id AND `empresa_id` = @empresaId AND `oauth_client_id` = @clientId LIMIT 1";
            update.Parameters.AddWithValue("@serie", series);
            update.Parameters.AddWithValue("@numero", initialNumber);
            if (columns.Contains("ambiente")) update.Parameters.AddWithValue("@ambiente", environmentValue);
            update.Parameters.AddWithValue("@clientId", oauthClientId);
            update.Parameters.AddWithValue("@id", id.Value);
            update.Parameters.AddWithValue("@empresaId", companyId);
            var affected = await update.ExecuteNonQueryAsync(cancellationToken);
            if (affected == 0)
            {
                throw new InvalidOperationException($"A serie {id.Value} de {displayType} nao foi localizada para esta empresa.");
            }
            savedId = id.Value;
        }
        else
        {
            var insertColumns = new List<string> { "empresa_id", "serie", "numeracao_inicial", "oauth_client_id" };
            var values = new List<string> { "@empresaId", "@serie", "@numero", "@clientId" };
            if (columns.Contains("padrao")) { insertColumns.Add("padrao"); values.Add("0"); }
            if (columns.Contains("ambiente")) { insertColumns.Add("ambiente"); values.Add("@ambiente"); }
            if (columns.Contains("tipo_serie")) { insertColumns.Add("tipo_serie"); values.Add("'DISPOSITIVO'"); }
            if (columns.Contains("created_at")) { insertColumns.Add("created_at"); values.Add("NOW()"); }
            if (columns.Contains("updated_at")) { insertColumns.Add("updated_at"); values.Add("NOW()"); }

            await using var insert = connection.CreateCommand();
            insert.CommandText =
                $"INSERT INTO `{table}` ({string.Join(", ", insertColumns.Select(x => $"`{x}`"))}) " +
                $"VALUES ({string.Join(", ", values)})";
            insert.Parameters.AddWithValue("@empresaId", companyId);
            insert.Parameters.AddWithValue("@serie", series);
            insert.Parameters.AddWithValue("@numero", initialNumber);
            insert.Parameters.AddWithValue("@clientId", oauthClientId);
            if (columns.Contains("ambiente")) insert.Parameters.AddWithValue("@ambiente", environmentValue);
            await insert.ExecuteNonQueryAsync(cancellationToken);
            savedId = insert.LastInsertedId;
        }

        return new FiscalSeriesInfo(
            savedId,
            displayType,
            companyId,
            series,
            initialNumber,
            environmentValue,
            false,
            oauthClientId,
            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
    }

    private static async Task LoadFiscalSeriesTableAsync(
        MySqlConnection connection,
        string table,
        string documentType,
        long companyId,
        string oauthClientId,
        List<FiscalSeriesInfo> destination,
        CancellationToken cancellationToken)
    {
        HashSet<string> columns;
        try
        {
            columns = await GetColumnsAsync(connection, table, cancellationToken);
        }
        catch (InvalidOperationException)
        {
            return;
        }

        if (!columns.Contains("id") || !columns.Contains("empresa_id") || !columns.Contains("serie") ||
            !columns.Contains("numeracao_inicial") || !columns.Contains("oauth_client_id"))
        {
            return;
        }

        var selected = new List<string> { "id", "empresa_id", "serie", "numeracao_inicial", "oauth_client_id" };
        foreach (var optional in new[] { "ambiente", "padrao", "updated_at" })
        {
            if (columns.Contains(optional)) selected.Add(optional);
        }

        var where = new List<string> { "`empresa_id` = @empresaId", "`oauth_client_id` = @clientId" };
        if (columns.Contains("deleted_at")) where.Add("`deleted_at` IS NULL");
        if (columns.Contains("tipo_serie")) where.Add("`tipo_serie` = 'DISPOSITIVO'");

        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT {string.Join(", ", selected.Select(x => $"`{x}`"))} FROM `{table}` " +
            $"WHERE {string.Join(" AND ", where)} ORDER BY `id`";
        command.Parameters.AddWithValue("@empresaId", companyId);
        command.Parameters.AddWithValue("@clientId", oauthClientId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            destination.Add(new FiscalSeriesInfo(
                Convert.ToInt64(reader["id"]),
                documentType,
                Convert.ToInt64(reader["empresa_id"]),
                Convert.ToString(reader["serie"]) ?? string.Empty,
                Convert.ToInt32(reader["numeracao_inicial"]),
                columns.Contains("ambiente") && !reader.IsDBNull(reader.GetOrdinal("ambiente")) ? Convert.ToInt32(reader["ambiente"]) : 2,
                columns.Contains("padrao") && !reader.IsDBNull(reader.GetOrdinal("padrao")) && Convert.ToInt32(reader["padrao"]) == 1,
                Convert.ToString(reader["oauth_client_id"]),
                columns.Contains("updated_at") && !reader.IsDBNull(reader.GetOrdinal("updated_at")) ? Convert.ToString(reader["updated_at"]) : null));
        }
    }

    private static string GenerateOAuthCredential(int length)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_.";
        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(length);
        var chars = new char[length];
        for (var i = 0; i < length; i++)
        {
            chars[i] = alphabet[bytes[i] % alphabet.Length];
        }
        return new string(chars);
    }

    public async Task<bool> TestConnectionAsync(
        string environmentKey,
        CancellationToken cancellationToken = default)
    {
        var environment = EnvironmentCatalog.Get(environmentKey);
        await using var connection = new MySqlConnection(BuildConnectionString(environment, database: null));
        await connection.OpenAsync(cancellationToken);
        return true;
    }

    private string BuildConnectionString(EnvironmentInfo environment, string? database)
    {
        var username = _secretStore.Get(SecretStore.DatabaseUsername);
        var password = _secretStore.Get(SecretStore.DatabasePassword);
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException(
                "Credenciais do banco nao disponiveis. Verifique a tela Configuracoes.");
        }

        var builder = new MySqlConnectionStringBuilder
        {
            Server = _overrideHost ?? environment.Host,
            Port = (uint)(_overridePort ?? environment.Port),
            UserID = username,
            Password = password,
            ConnectionTimeout = 5,
            DefaultCommandTimeout = 30,
            SslMode = MySqlSslMode.Preferred,
            AllowUserVariables = true
        };

        if (!string.IsNullOrWhiteSpace(database))
        {
            builder.Database = EnvironmentCatalog.NormalizeDatabaseName(database);
        }

        return builder.ConnectionString;
    }

    private static async Task<HashSet<string>> GetColumnsAsync(
        MySqlConnection connection,
        string table,
        CancellationToken cancellationToken)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SHOW COLUMNS FROM `{table}`";

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                result.Add(reader.GetString(0));
            }
        }
        catch (MySqlException ex)
        {
            throw new InvalidOperationException(
                $"Nao foi possivel consultar a tabela {table}. {ex.Message}", ex);
        }

        return result;
    }

    private static string? Pick(HashSet<string> columns, params string[] candidates) =>
        candidates.FirstOrDefault(columns.Contains);

    private static string Read(MySqlDataReader reader, string column)
    {
        try
        {
            var ordinal = reader.GetOrdinal(column);
            return reader.IsDBNull(ordinal) ? string.Empty : Convert.ToString(reader.GetValue(ordinal)) ?? string.Empty;
        }
        catch (IndexOutOfRangeException)
        {
            return string.Empty;
        }
    }

    private static string? ReadNullable(MySqlDataReader reader, string column)
    {
        var value = Read(reader, column);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static long? ReadNullableLong(MySqlDataReader reader, string column)
    {
        var value = Read(reader, column);
        return long.TryParse(value, out var parsed) ? parsed : null;
    }
}
