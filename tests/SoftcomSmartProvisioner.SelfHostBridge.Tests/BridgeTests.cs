using System.Text.Json;
using SoftcomSmartProvisioner.SelfHostBridge;
using Xunit;

namespace SoftcomSmartProvisioner.SelfHostBridge.Tests;

public sealed class BridgeTests
{
    private const string RootUrl = "https://cliente.softcomshop.com.br/softauth/device/add?client_id=ROOT-1&device_name=SELFHOST%20LOJA&empresa_name=EMPRESA%20TESTE";

    [Theory]
    [InlineData("4.0.0.0", true)]
    [InlineData("4.0.0.11", true)]
    [InlineData("4.1.0.12", true)]
    [InlineData("3.9.9.9", false)]
    public void SupportsSelfHost40AndNewer(string version, bool expected) =>
        Assert.Equal(expected, OfficialConfigurationGateway.IsSupportedVersion(version));

    [Fact]
    public void ParsesOfficialRootUrlWithoutInventingFields()
    {
        var root = RootDeviceUrl.Parse(RootUrl);

        Assert.Equal("ROOT-1", root.ClientId);
        Assert.Equal("SELFHOST LOJA", root.DeviceName);
        Assert.Equal("EMPRESA TESTE", root.CompanyName);
        Assert.Equal("https://cliente.softcomshop.com.br", root.BaseUrl);
    }

    [Fact]
    public void RejectsChildDeviceAsRoot()
    {
        var url = RootUrl.Replace("SELFHOST%20LOJA", "SELFHOST_FILHO", StringComparison.Ordinal);
        var error = Assert.Throws<BridgeValidationException>(() => RootDeviceUrl.Parse(url));
        Assert.Equal("invalid_root_device", error.Code);
    }

    [Fact]
    public void PreviewDoesNotSaveOrRegisterAndNeverReturnsSecret()
    {
        var gateway = NewGateway();
        var response = BridgeApplication.Execute(Command("preview"), _ => gateway);

        Assert.Equal(0, gateway.SaveCount);
        Assert.Equal(0, gateway.RegisterCount);
        Assert.Contains(response.Changes!, x => x.Field == "SoftcomShopSecretId" && Equals(x.To, "Será atualizado"));
        Assert.DoesNotContain("SEGREDO-TESTE", JsonSerializer.Serialize(response));
        Assert.DoesNotContain("ROOT-1", JsonSerializer.Serialize(response));
    }

    [Fact]
    public void ConfigureMergesExistingDocumentAndUsesOfficialRegistration()
    {
        var gateway = NewGateway();
        var response = BridgeApplication.Execute(Command("configure"), _ => gateway);

        Assert.True(response.Success);
        Assert.Equal(1, gateway.RegisterCount);
        Assert.Equal(1, gateway.SaveCount);
        Assert.Equal("preservado", gateway.Values["OutroModulo"]);
        Assert.Equal(ConfigurationEngine.SoftcomshopWeb, gateway.Values["TipoBancoDados"]);
        Assert.Equal("SEGREDO-TESTE", gateway.Values["SoftcomShopSecretId"]);
        Assert.True(response.Configuration!.HasClientSecret);
        Assert.DoesNotContain("SEGREDO-TESTE", JsonSerializer.Serialize(response));
    }

    [Fact]
    public void ConfigureRegistersAgainForSameRootAndReplacesPreviousSecret()
    {
        var gateway = NewGateway();
        var root = RootDeviceUrl.Parse(RootUrl);
        gateway.Values["SoftcomShopClientId"] = root.ClientId;
        gateway.Values["SoftcomShopUrlBase"] = root.BaseUrl;
        gateway.Values["SoftcomShopDeviceId"] = Environment.MachineName;
        gateway.Values["SoftcomShopSecretId"] = "SEGREDO-EXISTENTE";

        BridgeApplication.Execute(Command("configure"), _ => gateway);

        Assert.Equal(1, gateway.RegisterCount);
        Assert.Equal("SEGREDO-TESTE", gateway.Values["SoftcomShopSecretId"]);
    }

    [Fact]
    public void ConfigureMergesTableDatabaseWithoutChangingOtherModules()
    {
        var gateway = NewGateway();
        var command = Command("configure");
        command.Desired!.TableDatabase = new TableDatabaseInput
        {
            Server = "servidor-mesas",
            Port = "3306",
            User = "usuario-mesas",
            Password = "SENHA-MYSQL-TESTE",
            Database = "mesas"
        };

        var response = BridgeApplication.Execute(command, _ => gateway);

        Assert.Equal("servidor-mesas", gateway.Values["MysqlServidor"]);
        Assert.Equal("3306", gateway.Values["MysqlPorta"]);
        Assert.Equal("usuario-mesas", gateway.Values["MysqlUsuario"]);
        Assert.Equal("SENHA-MYSQL-TESTE", gateway.Values["MysqlSenha"]);
        Assert.Equal("mesas", gateway.Values["MysqlDatabase"]);
        Assert.Equal("preservado", gateway.Values["OutroModulo"]);
        Assert.True(response.Configuration!.IsTableDatabaseComplete);
        Assert.DoesNotContain("SENHA-MYSQL-TESTE", JsonSerializer.Serialize(response));
    }

    [Fact]
    public void EmptyTableDatabasePasswordPreservesExistingPassword()
    {
        var gateway = NewGateway();
        gateway.Values["MysqlSenha"] = "SENHA-EXISTENTE";
        var command = Command("configure");
        command.Desired!.TableDatabase = new TableDatabaseInput
        {
            Server = "servidor-mesas",
            Port = "3306",
            User = "usuario-mesas",
            Password = "",
            Database = "mesas"
        };

        var response = BridgeApplication.Execute(command, _ => gateway);

        Assert.Equal("SENHA-EXISTENTE", gateway.Values["MysqlSenha"]);
        Assert.True(response.Configuration!.HasMysqlPassword);
        Assert.DoesNotContain("SENHA-EXISTENTE", JsonSerializer.Serialize(response));
    }

    [Fact]
    public void IncompleteTableDatabaseIsRejectedBeforeRegistrationOrSave()
    {
        var gateway = NewGateway();
        var command = Command("configure");
        command.Desired!.TableDatabase = new TableDatabaseInput
        {
            Server = "servidor-mesas",
            Port = "3306",
            User = "",
            Password = "SENHA-MYSQL-TESTE",
            Database = "mesas"
        };

        var error = Assert.Throws<BridgeValidationException>(() => BridgeApplication.Execute(command, _ => gateway));

        Assert.Equal("table_database_incomplete", error.Code);
        Assert.Equal(0, gateway.RegisterCount);
        Assert.Equal(0, gateway.SaveCount);
    }

    [Fact]
    public void ConfigureSoftshopDesktopUsesOfficialFieldsWithoutRootRegistration()
    {
        var gateway = NewGateway();
        var command = DesktopCommand("configure", "SENHA-SQL-TESTE");

        var response = BridgeApplication.Execute(command, _ => gateway);

        Assert.True(response.Success);
        Assert.Equal(0, gateway.RegisterCount);
        Assert.Equal(1, gateway.SaveCount);
        Assert.Equal(ConfigurationEngine.SoftshopDesktop, gateway.Values["TipoBancoDados"]);
        Assert.Equal("SERVIDOR\\INSTANCIA", gateway.Values["Servidor"]);
        Assert.Equal("sa", gateway.Values["Usuario"]);
        Assert.Equal("BANCO_TESTE", gateway.Values["BancoDados"]);
        Assert.True(response.Configuration!.IsDesktopDatabaseComplete);
        Assert.DoesNotContain("SENHA-SQL-TESTE", JsonSerializer.Serialize(response));
    }

    [Fact]
    public void SoftshopDesktopPreservesExistingPasswordWhenInputIsEmpty()
    {
        var gateway = NewGateway();
        gateway.Values["Senha"] = "SENHA-EXISTENTE";

        var response = BridgeApplication.Execute(DesktopCommand("configure", ""), _ => gateway);

        Assert.Equal("SENHA-EXISTENTE", gateway.Values["Senha"]);
        Assert.True(response.Configuration!.HasDatabasePassword);
        Assert.DoesNotContain("SENHA-EXISTENTE", JsonSerializer.Serialize(response));
    }

    [Fact]
    public void IncompleteSoftshopDesktopIsRejectedBeforeSave()
    {
        var gateway = NewGateway();
        var command = DesktopCommand("configure", "SENHA-SQL-TESTE");
        command.Desired!.DesktopDatabase!.Database = "";

        var error = Assert.Throws<BridgeValidationException>(() => BridgeApplication.Execute(command, _ => gateway));

        Assert.Equal("desktop_database_incomplete", error.Code);
        Assert.Equal(0, gateway.RegisterCount);
        Assert.Equal(0, gateway.SaveCount);
    }

    [Fact]
    public void UnknownActionIsRejected()
    {
        var error = Assert.Throws<BridgeValidationException>(() =>
            BridgeApplication.Execute(new BridgeCommand { Action = "delete", InstallRoot = "X" }, _ => NewGateway()));
        Assert.Equal("unknown_action", error.Code);
    }

    [Fact]
    public void InvalidJsonIsRejectedWithoutEchoingInput()
    {
        var error = Assert.Throws<BridgeValidationException>(() => BridgeProtocol.ParseCommand("{client_secret:valor}"));
        Assert.Equal("invalid_json", error.Code);
        Assert.DoesNotContain("valor", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PreviewRequiresRootDevice()
    {
        var command = Command("preview");
        command.Desired!.RootDevice = null;
        var error = Assert.Throws<BridgeValidationException>(() => BridgeApplication.Execute(command, _ => NewGateway()));
        Assert.Equal("root_device_missing", error.Code);
    }

    [Fact]
    public void InvalidPortIsRejectedBeforeAnyRegistrationOrSave()
    {
        var gateway = NewGateway();
        var command = Command("configure");
        command.Desired!.PortaHttp = 70000;
        var error = Assert.Throws<BridgeValidationException>(() => BridgeApplication.Execute(command, _ => gateway));
        Assert.Equal("invalid_port", error.Code);
        Assert.Equal(0, gateway.RegisterCount);
        Assert.Equal(0, gateway.SaveCount);
    }

    [Fact]
    public void RegistrationFailureDoesNotSavePartialConfiguration()
    {
        var gateway = NewGateway();
        gateway.FailRegistration = true;
        Assert.Throws<BridgeValidationException>(() => BridgeApplication.Execute(Command("configure"), _ => gateway));
        Assert.Equal(0, gateway.SaveCount);
        Assert.Equal("preservado", gateway.Values["OutroModulo"]);
    }

    [Fact]
    public void SanitizerHidesSensitiveAssignmentsAndBearerTokens()
    {
        var cleaned = SecretSanitizer.Clean("client_secret=abc Authorization: Bearer token-real senha=123");
        Assert.DoesNotContain("abc", cleaned);
        Assert.DoesNotContain("token-real", cleaned);
        Assert.DoesNotContain("123", cleaned);
    }

    private static BridgeCommand Command(string action) => new()
    {
        Action = action,
        InstallRoot = "C:\\SelfHost",
        Desired = new DesiredConfiguration
        {
            PortaHttp = 7711,
            SmartEnabled = true,
            RootDevice = new RootDeviceInput { DeviceUrl = RootUrl }
        }
    };

    private static BridgeCommand DesktopCommand(string action, string password) => new()
    {
        Action = action,
        InstallRoot = "C:\\SelfHost",
        Desired = new DesiredConfiguration
        {
            Backend = "softshop",
            PortaHttp = 7711,
            SmartEnabled = true,
            DesktopDatabase = new DesktopDatabaseInput
            {
                Server = "SERVIDOR\\INSTANCIA",
                Port = "",
                User = "sa",
                Password = password,
                Database = "BANCO_TESTE"
            }
        }
    };

    private static FakeGateway NewGateway() => new(new Dictionary<string, object?>
    {
        ["TipoBancoDados"] = "Softshop (Desktop)",
        ["PortaHTTP"] = 7711,
        ["SoftcomShopUrl"] = null,
        ["SoftcomShopUrlBase"] = null,
        ["SoftcomShopEmpresa"] = null,
        ["SoftcomShopDevice"] = null,
        ["SoftcomShopDeviceId"] = null,
        ["SoftcomShopClientId"] = null,
        ["SoftcomShopSecretId"] = null,
        ["SoftcomShopSituacao"] = null,
        ["SmartEnabled"] = false,
        ["DevicesEnabled"] = false,
        ["RelayServer"] = null,
        ["RelayClientId"] = null,
        ["RelayServerClientId"] = null,
        ["Servidor"] = null,
        ["Porta"] = null,
        ["Usuario"] = null,
        ["Senha"] = null,
        ["BancoDados"] = null,
        ["MysqlServidor"] = null,
        ["MysqlPorta"] = null,
        ["MysqlUsuario"] = null,
        ["MysqlSenha"] = null,
        ["MysqlDatabase"] = null,
        ["OutroModulo"] = "preservado"
    });

    private sealed class FakeGateway(Dictionary<string, object?> values) : IConfigurationGateway
    {
        public string Version => "4.0.0.11";
        public string Generation => "SelfHost 4.0";
        public Dictionary<string, object?> Values { get; } = values;
        public int SaveCount { get; private set; }
        public int RegisterCount { get; private set; }
        public bool FailRegistration { get; set; }
        public IConfigurationDocument Load() => new DictionaryConfigurationDocument(Values);
        public void Save(IConfigurationDocument document) => SaveCount++;
        public string RegisterRootDevice(RootDeviceUrl rootDevice)
        {
            RegisterCount++;
            if (FailRegistration) throw new BridgeValidationException("root_registration_failed", "Falha simulada.");
            return "SEGREDO-TESTE";
        }
        public void Dispose() { }
    }
}
