using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace SoftcomSmartProvisioner.SelfHostBridge;

public interface IConfigurationGateway : IDisposable
{
    string Version { get; }
    string Generation { get; }
    IConfigurationDocument Load();
    void Save(IConfigurationDocument document);
    string RegisterRootDevice(RootDeviceUrl rootDevice);
}

public sealed class OfficialConfigurationGateway : IConfigurationGateway
{
    private const string ApiFileName = "SelfHost.API.dll";
    private readonly string _installRoot;
    private readonly SelfHostLoadContext _loadContext;
    private readonly Assembly _api;
    private readonly MethodInfo _load;
    private readonly MethodInfo _save;

    public string Version { get; }
    public string Generation { get; }

    public OfficialConfigurationGateway(string installRoot)
    {
        _installRoot = ValidateInstallation(installRoot, out var apiPath, out var version);
        Version = version.ToString();
        Generation = version >= new Version(4, 1, 0, 0) ? "SelfHost 4.1+" : "SelfHost 4.0";
        _loadContext = new SelfHostLoadContext(_installRoot);
        try
        {
            _api = _loadContext.LoadFromAssemblyPath(apiPath);
        }
        catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or FileNotFoundException)
        {
            throw new BridgeValidationException("runtime_incompatible", "O runtime do Bridge é incompatível com os assemblies desta instalação do SelfHost.");
        }

        var controller = _api.GetType("Softcom.Integracao.API.Controllers.Configuracoes.ConfiguracoesController", throwOnError: false)
            ?? throw new BridgeValidationException("official_api_missing", "ConfiguracoesController não foi localizado em SelfHost.API.dll.");
        _load = controller.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(x => x.Name == "CarregarConfig")
            ?? throw new BridgeValidationException("official_api_missing", "ConfiguracoesController.CarregarConfig não foi localizado.");
        _save = controller.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(x => x.Name == "GravarConfig")
            ?? throw new BridgeValidationException("official_api_missing", "ConfiguracoesController.GravarConfig não foi localizado.");
    }

    public IConfigurationDocument Load()
    {
        try
        {
            var value = _load.Invoke(null, BuildArguments(_load, _installRoot))
                ?? throw new BridgeValidationException("configuration_missing", "O SelfHost ainda não possui configuração ativa.");
            return new ReflectionConfigurationDocument(value);
        }
        catch (TargetInvocationException ex)
        {
            throw new BridgeValidationException("read_failed", SecretSanitizer.Clean(ex.InnerException?.Message ?? ex.Message));
        }
    }

    public void Save(IConfigurationDocument document)
    {
        try
        {
            var arguments = BuildArguments(_save, _installRoot);
            if (arguments.Length == 0)
                throw new BridgeValidationException("official_api_missing", "ConfiguracoesController.GravarConfig não recebe o objeto de configuração.");
            arguments[0] = document.RawObject;
            _save.Invoke(null, arguments);
        }
        catch (TargetInvocationException ex)
        {
            throw new BridgeValidationException("write_failed", SecretSanitizer.Clean(ex.InnerException?.Message ?? ex.Message));
        }
    }

    public string RegisterRootDevice(RootDeviceUrl rootDevice)
    {
        try
        {
            var serviceType = _api.GetType("Softcom.Integracao.API.Services.SoftcomshopService", throwOnError: false)
                ?? throw new BridgeValidationException("official_api_missing", "SoftcomshopService não foi localizado em SelfHost.API.dll.");
            var addDeviceType = _api.GetType("Softcom.Integracao.API.Models.Services.Softcomshop.AddDevice", throwOnError: false)
                ?? throw new BridgeValidationException("official_api_missing", "O modelo oficial AddDevice não foi localizado.");
            var service = Activator.CreateInstance(serviceType, rootDevice.BaseUrl)
                ?? throw new BridgeValidationException("official_api_missing", "SoftcomshopService não pôde ser criado.");
            var request = Activator.CreateInstance(addDeviceType)
                ?? throw new BridgeValidationException("official_api_missing", "AddDevice não pôde ser criado.");
            Set(request, "ClientId", rootDevice.ClientId);
            Set(request, "DeviceId", Environment.MachineName);
            Set(request, "DeviceName", rootDevice.DeviceName);

            var method = serviceType.GetMethod("AddRootDevice", BindingFlags.Public | BindingFlags.Instance)
                ?? throw new BridgeValidationException("official_api_missing", "SoftcomshopService.AddRootDevice não foi localizado.");
            var invocation = method.Invoke(service, [request]) as Task
                ?? throw new BridgeValidationException("root_registration_failed", "O método oficial de vínculo não retornou uma operação válida.");
            invocation.GetAwaiter().GetResult();
            var response = invocation.GetType().GetProperty("Result")?.GetValue(invocation);
            var data = response?.GetType().GetProperty("Data")?.GetValue(response);
            var secret = data?.GetType().GetProperty("ClientSecret")?.GetValue(data)?.ToString();
            if (string.IsNullOrWhiteSpace(secret))
                throw new BridgeValidationException("root_registration_failed", "O Softcomshop não retornou a credencial do dispositivo raiz.");
            return secret;
        }
        catch (BridgeValidationException) { throw; }
        catch (TargetInvocationException ex)
        {
            throw new BridgeValidationException("root_registration_failed", SecretSanitizer.Clean(ex.InnerException?.Message ?? ex.Message));
        }
        catch (Exception ex)
        {
            throw new BridgeValidationException("root_registration_failed", SecretSanitizer.Clean(ex.GetBaseException().Message));
        }
    }

    public void Dispose() => _loadContext.Unload();

    public static string ValidateInstallation(string installRoot, out string apiPath, out Version version)
    {
        if (string.IsNullOrWhiteSpace(installRoot))
            throw new BridgeValidationException("install_root_missing", "Informe installRoot.");
        var root = Path.GetFullPath(installRoot.Trim());
        if (!Directory.Exists(root))
            throw new BridgeValidationException("install_root_missing", "A instalação informada do SelfHost não existe.");
        apiPath = Path.Combine(root, ApiFileName);
        if (!File.Exists(apiPath))
            throw new BridgeValidationException("api_dll_missing", "SelfHost.API.dll não foi localizado na instalação.");
        var exe = Path.Combine(root, "SelfHost.exe");
        if (!File.Exists(exe))
            throw new BridgeValidationException("selfhost_exe_missing", "SelfHost.exe não foi localizado na instalação.");
        var versionText = FileVersionInfo.GetVersionInfo(exe).FileVersion ?? string.Empty;
        if (!TryVersion(versionText, out version) || !IsSupportedVersion(versionText))
            throw new BridgeValidationException("unsupported_version", "Somente SelfHost 4.0 ou superior é suportado por esta configuração.");
        return root;
    }

    public static bool IsSupportedVersion(string versionText) =>
        TryVersion(versionText, out var version) && version >= new Version(4, 0, 0, 0);

    private static bool TryVersion(string text, out Version version)
    {
        var match = System.Text.RegularExpressions.Regex.Match(text, @"\d+(?:\.\d+){1,3}");
        var parts = match.Success ? match.Value.Split('.').ToList() : [];
        while (parts.Count < 4) parts.Add("0");
        return System.Version.TryParse(string.Join('.', parts.Take(4)), out version!);
    }

    private static void Set(object target, string propertyName, object value) =>
        target.GetType().GetProperty(propertyName)?.SetValue(target, value);

    private static object?[] BuildArguments(MethodInfo method, string installRoot)
    {
        var parameters = method.GetParameters();
        var args = new object?[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
        {
            if (parameters[i].ParameterType == typeof(string) &&
                (parameters[i].Name?.Contains("diretorio", StringComparison.OrdinalIgnoreCase) == true ||
                 parameters[i].Name?.Contains("path", StringComparison.OrdinalIgnoreCase) == true))
                args[i] = installRoot;
            else if (parameters[i].HasDefaultValue) args[i] = parameters[i].DefaultValue;
            else args[i] = parameters[i].ParameterType.IsValueType ? Activator.CreateInstance(parameters[i].ParameterType) : null;
        }
        return args;
    }

    private sealed class SelfHostLoadContext(string root) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            var candidates = new[]
            {
                Path.Combine(root, name.Name + ".dll"),
                Path.Combine(root, "binaries", name.Name + ".dll")
            };
            var candidate = candidates.FirstOrDefault(File.Exists);
            return candidate is null ? null : LoadFromAssemblyPath(candidate);
        }

        protected override nint LoadUnmanagedDll(string unmanagedDllName)
        {
            var fileName = unmanagedDllName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                ? unmanagedDllName
                : unmanagedDllName + ".dll";
            var runtime = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => "win-x64",
                Architecture.X86 => "win-x86",
                Architecture.Arm64 => "win-arm64",
                Architecture.Arm => "win-arm",
                _ => string.Empty
            };
            var candidates = new[]
            {
                Path.Combine(root, "runtimes", runtime, "native", fileName),
                Path.Combine(root, "binaries", "runtimes", runtime, "native", fileName),
                Path.Combine(root, fileName),
                Path.Combine(root, "binaries", fileName),
                Path.Combine(root, "x64", fileName),
                Path.Combine(root, "x86", fileName)
            };
            var candidate = candidates.FirstOrDefault(File.Exists);
            return candidate is null ? nint.Zero : LoadUnmanagedDllFromPath(candidate);
        }
    }
}
