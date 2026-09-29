using System.Reflection;
using SoftcomSmartProvisioner.Services;

namespace SoftcomSmartProvisioner.Tests;

public sealed class SmartUiAutomationBoundsTests
{
    [Fact]
    public void ParsesStoneSettingsBoundsIncludingParentOffset()
    {
        const string dump = """
                View Hierarchy:
                  DecorView@ca68a71[LoginActivity]
                    android.widget.LinearLayout{8de4a77 V.E...... ........ 0,0-720,1504}
                      android.widget.FrameLayout{15284fe V.E...... ........ 0,48-720,1504}
                        androidx.appcompat.widget.ContentFrameLayout{cb775 V.E...... ........ 0,0-720,1456 app:id/content}
                          androidx.constraintlayout.widget.ConstraintLayout{ecdf30a VFE...... .F...... 0,0-720,1456}
                            androidx.appcompat.widget.AppCompatImageButton{d2c0305 VFED..C.. ........ 530,625-670,725 #7f0a00d1 app:id/btn_config}
            """;

        var result = Parse(dump, "app:id/btn_config");

        Assert.True(Read<bool>(result, "Success"));
        Assert.Equal(600, Read<int>(result, "CenterX"));
        Assert.Equal(723, Read<int>(result, "CenterY"));
    }

    [Fact]
    public void ParsesMercadoPagoConfirmBoundsAtCurrentLayoutPosition()
    {
        const string dump = """
                View Hierarchy:
                  DecorView@d65013b[EmpresaAddActivity]
                    android.widget.LinearLayout{a14af2b V.E...... ........ 0,0-720,1320}
                      android.widget.FrameLayout{3803b7a V.E...... ........ 0,0-720,1320}
                        androidx.coordinatorlayout.widget.CoordinatorLayout{fe110f V.ED..... ........ 0,0-720,1320 app:id/activity_device}
                          android.widget.LinearLayout{25d15b1 V.E...... ........ 0,160-720,957 app:id/device_content_main}
                            android.widget.ScrollView{8e76c34 VFED.V... ........ 16,16-704,781}
                              android.widget.LinearLayout{b7dd8a3 V.E...... ........ 0,0-688,765}
                                android.widget.LinearLayout{f81a8d0 V.E...... ........ 0,637-688,765}
                                  androidx.appcompat.widget.AppCompatButton{c8470c9 VFED..CL. ........ 344,32-688,128 #7f0a00dd app:id/btn_confirmar}
            """;

        var result = Parse(dump, "app:id/btn_confirmar");

        Assert.True(Read<bool>(result, "Success"));
        Assert.Equal(532, Read<int>(result, "CenterX"));
        Assert.Equal(893, Read<int>(result, "CenterY"));
    }

    [Fact]
    public void FallsBackToViewRectangleWhenManufacturerOmitsHierarchyHeader()
    {
        const string dump =
            "androidx.appcompat.widget.AppCompatImageButton{d2c0305 VFED..C.. ........ 530,625-670,725 #7f0a00d1 app:id/btn_config}";

        var result = Parse(dump, "app:id/btn_config");

        Assert.True(Read<bool>(result, "Success"));
        Assert.Equal(600, Read<int>(result, "CenterX"));
        Assert.Equal(675, Read<int>(result, "CenterY"));
    }

    [Fact]
    public void MercadoPagoN950TefLayoutUsesMeasuredScrollableAreaAboveNavigationBar()
    {
        var layout = GetTefLayout(720, 1440, "mercadopagon950");

        Assert.Equal("Mercado Pago N950 720x1440", Read<string>(layout, "Name"));
        Assert.True(Read<bool>(layout, "RequiresManualScroll"));
        Assert.True(Read<bool>(layout, "UseCoordinateConclusion"));
        Assert.True(Read<bool>(layout, "RequiresActivityGuard"));
        Assert.True(Read<bool>(layout, "UseUiFieldBounds"));
        Assert.Equal(1248, Read<int>(layout, "ConfirmY"));
        Assert.InRange(Read<int>(layout, "ConfirmY"), 1, 1319);
        Assert.Equal(1024, Read<int>(layout, "TokenY"));
    }

    [Fact]
    public void PositivoL400TefLayoutUsesMeasuredFieldsAndControlledScroll()
    {
        var layout = GetTefLayout(720, 1600, "stonel400");

        Assert.Equal("Positivo L400 720x1600", Read<string>(layout, "Name"));
        Assert.True(Read<bool>(layout, "RequiresManualScroll"));
        Assert.True(Read<bool>(layout, "UseCoordinateConclusion"));
        Assert.True(Read<bool>(layout, "RequiresActivityGuard"));
        Assert.True(Read<bool>(layout, "UseUiFieldBounds"));
        Assert.Equal(527, Read<int>(layout, "NameY"));
        Assert.Equal(912, Read<int>(layout, "ManualY"));
        Assert.Equal(873, Read<int>(layout, "CnpjY"));
        Assert.Equal(1037, Read<int>(layout, "CompanyY"));
        Assert.Equal(1201, Read<int>(layout, "TokenY"));
        Assert.Equal(1432, Read<int>(layout, "ConfirmY"));
        Assert.Equal(1066, Read<int>(layout, "ConcludeY"));
    }

    [Fact]
    public void ComposeRootContainingAllLabelsDoesNotRedirectCnpjToToken()
    {
        var nodeType = typeof(SmartUiAutomationService).GetNestedType(
            "UiNode",
            BindingFlags.NonPublic)
            ?? throw new MissingMemberException(typeof(SmartUiAutomationService).FullName, "UiNode");

        object Node(
            string text,
            string className,
            string searchText,
            int top,
            int bottom) => Activator.CreateInstance(
                nodeType,
                text,
                string.Empty,
                string.Empty,
                className,
                "softcom.mobile.smart2",
                searchText,
                false,
                true,
                64,
                top,
                656,
                bottom)
            ?? throw new InvalidOperationException("Nao foi possivel criar o no de teste.");

        var values = new[]
        {
            Node(string.Empty, "android.widget.FrameLayout", "CNPJ Empresa ID Token", 0, 1504),
            Node("CNPJ", "android.widget.TextView", "CNPJ", 780, 828),
            Node(string.Empty, "android.widget.EditText", string.Empty, 825, 921),
            Node("Empresa ID", "android.widget.TextView", "Empresa ID", 944, 992),
            Node(string.Empty, "android.widget.EditText", string.Empty, 989, 1085),
            Node("Token", "android.widget.TextView", "Token", 1108, 1156),
            Node(string.Empty, "android.widget.EditText", string.Empty, 1153, 1249)
        };
        var typedNodes = Array.CreateInstance(nodeType, values.Length);
        for (var index = 0; index < values.Length; index++)
        {
            typedNodes.SetValue(values[index], index);
        }

        var method = typeof(SmartUiAutomationService).GetMethod(
            "FindEditableBelowLabels",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(
                typeof(SmartUiAutomationService).FullName,
                "FindEditableBelowLabels");

        var cnpj = method.Invoke(null, new object[] { typedNodes, new[] { "cnpj" } })
            ?? throw new InvalidOperationException("O campo CNPJ nao foi localizado.");
        var company = method.Invoke(null, new object[] { typedNodes, new[] { "empresa id" } })
            ?? throw new InvalidOperationException("O campo Empresa ID nao foi localizado.");
        var token = method.Invoke(null, new object[] { typedNodes, new[] { "token" } })
            ?? throw new InvalidOperationException("O campo Token nao foi localizado.");

        Assert.Equal(873, Read<int>(cnpj, "CenterY"));
        Assert.Equal(1037, Read<int>(company, "CenterY"));
        Assert.Equal(1201, Read<int>(token, "CenterY"));
    }

    [Fact]
    public void StoneSuccessDialogLocatesClickableConcludeButton()
    {
        var nodeType = typeof(SmartUiAutomationService).GetNestedType(
            "UiNode",
            BindingFlags.NonPublic)
            ?? throw new MissingMemberException(typeof(SmartUiAutomationService).FullName, "UiNode");

        object Node(
            string text,
            string className,
            string searchText,
            bool clickable,
            int left,
            int top,
            int right,
            int bottom) => Activator.CreateInstance(
                nodeType,
                text,
                string.Empty,
                string.Empty,
                className,
                "softcom.mobile.smart2",
                searchText,
                clickable,
                true,
                left,
                top,
                right,
                bottom)
            ?? throw new InvalidOperationException("Nao foi possivel criar o no de teste.");

        var values = new[]
        {
            Node(string.Empty, "android.view.View", "Chaves verificadas com sucesso! Concluir", false, 40, 390, 680, 1162),
            Node("Chaves verificadas com sucesso!", "android.widget.TextView", "Chaves verificadas com sucesso!", false, 88, 726, 632, 808),
            Node(string.Empty, "android.view.View", "Concluir", true, 88, 1018, 632, 1114),
            Node("Concluir", "android.widget.TextView", "Concluir", false, 306, 1046, 414, 1086)
        };
        var typedNodes = Array.CreateInstance(nodeType, values.Length);
        for (var index = 0; index < values.Length; index++)
        {
            typedNodes.SetValue(values[index], index);
        }

        var find = typeof(SmartUiAutomationService).GetMethod(
            "FindByLabels",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(typeof(SmartUiAutomationService).FullName, "FindByLabels");
        var success = typeof(SmartUiAutomationService).GetMethod(
            "IsSmartTefKeySuccess",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(typeof(SmartUiAutomationService).FullName, "IsSmartTefKeySuccess");

        var conclude = find.Invoke(null, new object[] { typedNodes, new[] { "concluir" } })
            ?? throw new InvalidOperationException("O botao Concluir nao foi localizado.");

        Assert.True((bool)(success.Invoke(null, new object[] { typedNodes }) ?? false));
        Assert.Equal(360, Read<int>(conclude, "CenterX"));
        Assert.Equal(1066, Read<int>(conclude, "CenterY"));
    }

    [Theory]
    [InlineData(1080, 1920, 832, 1020)]
    [InlineData(540, 960, 416, 510)]
    public void K2SynchronizationOkPointScalesWithoutUiAutomator(
        int width,
        int height,
        int expectedX,
        int expectedY)
    {
        var method = typeof(SmartUiAutomationService).GetMethod(
            "GetLegacy80LargeSynchronizationOkPoint",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(
                typeof(SmartUiAutomationService).FullName,
                "GetLegacy80LargeSynchronizationOkPoint");

        var point = ((int X, int Y))(method.Invoke(null, new object[] { width, height })
            ?? throw new InvalidOperationException("O ponto do OK do K2 nao foi calculado."));

        Assert.Equal(expectedX, point.X);
        Assert.Equal(expectedY, point.Y);
    }

    [Theory]
    [InlineData(1080, 1920, 540, 1089)]
    [InlineData(540, 960, 270, 544)]
    public void K2Smart81SynchronizationOkPointUsesComposeDialogLayout(
        int width,
        int height,
        int expectedX,
        int expectedY)
    {
        var method = typeof(SmartUiAutomationService).GetMethod(
            "GetSmart81K2SynchronizationOkPoint",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(
                typeof(SmartUiAutomationService).FullName,
                "GetSmart81K2SynchronizationOkPoint");

        var point = ((int X, int Y))(method.Invoke(null, new object[] { width, height })
            ?? throw new InvalidOperationException("O ponto do OK Compose do K2 nao foi calculado."));

        Assert.Equal(expectedX, point.X);
        Assert.Equal(expectedY, point.Y);
    }

    [Fact]
    public void DeviceUrlVerificationDetectsTruncatedKeyWithoutExposingValues()
    {
        var matches = typeof(SmartUiAutomationService).GetMethod(
            "DeviceLinkUrlsMatch",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(typeof(SmartUiAutomationService).FullName, "DeviceLinkUrlsMatch");
        var summary = typeof(SmartUiAutomationService).GetMethod(
            "BuildSafeInputSummary",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(typeof(SmartUiAutomationService).FullName, "BuildSafeInputSummary");

        const string expected = "https://cliente.meusoftcom.com.br/softauth/device/add?client_id=abc&key=segredo";
        const string actual = "https://cliente.meusoftcom.com.br/softauth/device/add?client_id=abc";

        Assert.False((bool)(matches.Invoke(null, new object[] { expected, actual }) ?? true));
        var safeSummary = (string)(summary.Invoke(null, new object[] { expected, actual }) ?? string.Empty);
        Assert.Contains("key", safeSummary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("segredo", safeSummary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("abc", safeSummary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DeviceUrlVerificationReportsOnlySafeLengthsWhenParameterNamesMatch()
    {
        var summary = typeof(SmartUiAutomationService).GetMethod(
            "BuildSafeInputSummary",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(typeof(SmartUiAutomationService).FullName, "BuildSafeInputSummary");

        const string expected = "https://host/device/add?client_id=abcdef";
        const string actual = "https://host/device/add?client_id=abcde";
        var safeSummary = (string)(summary.Invoke(null, new object[] { expected, actual }) ?? string.Empty);

        Assert.Contains($"esperado: {expected.Length}", safeSummary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"recebido: {actual.Length}", safeSummary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("abcdef", safeSummary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("abcde", safeSummary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void N950AcceptsAccessibilityPrefixWhenHostPathAndClientIdMatch()
    {
        const string expected = "https://cliente.meusoftcom.com.br:7711/softauth/device/add?client_id=abc123&empresa_name=Loja&empresa_cnpj=123&device_name=N950";
        const string accessibilityValue = "https://cliente.meusoftcom.com.br:7711/softauth/device/add?client_id=abc123";

        Assert.True(CanTrustN950AccessibilityPrefix(expected, accessibilityValue));
    }

    [Theory]
    [InlineData(
        "https://cliente.meusoftcom.com.br/softauth/device/add?client_id=correto&empresa_name=Loja",
        "https://cliente.meusoftcom.com.br/softauth/device/add?client_id=errado")]
    [InlineData(
        "https://cliente.meusoftcom.com.br/softauth/device/add?client_id=correto&empresa_name=Loja",
        "https://outro.meusoftcom.com.br/softauth/device/add?client_id=correto")]
    [InlineData(
        "https://cliente.meusoftcom.com.br/softauth/device/add?client_id=correto&empresa_name=Loja",
        "https://cliente.meusoftcom.com.br/softauth/outra-rota?client_id=correto")]
    [InlineData(
        "https://cliente.meusoftcom.com.br/softauth/device/add?client_id=correto&empresa_name=Loja",
        "https://cliente.meusoftcom.com.br/softauth/device/add?client_id=correto&empresa_name=Outra")]
    public void N950RejectsUnsafeAccessibilityValues(string expected, string accessibilityValue)
    {
        Assert.False(CanTrustN950AccessibilityPrefix(expected, accessibilityValue));
    }

    private static bool CanTrustN950AccessibilityPrefix(string expected, string actual)
    {
        var method = typeof(SmartUiAutomationService).GetMethod(
            "CanTrustN950AccessibilityPrefix",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(
                typeof(SmartUiAutomationService).FullName,
                "CanTrustN950AccessibilityPrefix");

        return (bool)(method.Invoke(null, new object[] { expected, actual }) ?? false);
    }

    [Fact]
    public void UiSummaryHidesDeviceUrlParameterValues()
    {
        var sanitize = typeof(SmartUiAutomationService).GetMethod(
            "SanitizeUiSummaryValue",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(typeof(SmartUiAutomationService).FullName, "SanitizeUiSummaryValue");

        var result = (string)(sanitize.Invoke(null, new object[]
        {
            "https://cliente.meusoftcom.com.br/softauth/device/add?client_id=abc&key=segredo"
        }) ?? string.Empty);

        Assert.Contains("client_id=[OCULTO]", result, StringComparison.Ordinal);
        Assert.Contains("key=[OCULTO]", result, StringComparison.Ordinal);
        Assert.DoesNotContain("segredo", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("abc", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingRequestKeyIsRecognizedAsImmediateLinkFailure()
    {
        var nodeType = typeof(SmartUiAutomationService).GetNestedType(
            "UiNode",
            BindingFlags.NonPublic)
            ?? throw new MissingMemberException(typeof(SmartUiAutomationService).FullName, "UiNode");
        var node = Activator.CreateInstance(
            nodeType,
            "A requisição não contém chave.",
            string.Empty,
            string.Empty,
            "android.widget.TextView",
            "softcom.mobile.smart2",
            "A requisição não contém chave.",
            false,
            true,
            0,
            0,
            600,
            80)
            ?? throw new InvalidOperationException("Nao foi possivel criar o no de teste.");
        var nodes = Array.CreateInstance(nodeType, 1);
        nodes.SetValue(node, 0);

        var method = typeof(SmartUiAutomationService).GetMethod(
            "IsSynchronizationFailure",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(typeof(SmartUiAutomationService).FullName, "IsSynchronizationFailure");

        Assert.True((bool)(method.Invoke(null, new object[] { nodes }) ?? false));
    }

    [Theory]
    [InlineData("mercadopagon950", true)]
    [InlineData("stone", false)]
    [InlineData("totemk2", false)]
    [InlineData(null, false)]
    public void QuotedUrlInputIsRestrictedToMercadoPagoN950(string? profile, bool expected)
    {
        var method = typeof(SmartUiAutomationService).GetMethod(
            "ShouldUseQuotedDeviceLinkInput",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(typeof(SmartUiAutomationService).FullName, "ShouldUseQuotedDeviceLinkInput");

        Assert.Equal(expected, (bool)(method.Invoke(null, new object?[] { profile }) ?? false));
    }

    [Fact]
    public void N950SafeDescriptorShowsStructureWithoutValues()
    {
        var method = typeof(SmartUiAutomationService).GetMethod(
            "BuildSafeDeviceLinkDescriptor",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(typeof(SmartUiAutomationService).FullName, "BuildSafeDeviceLinkDescriptor");

        const string url = "https://host/softauth/device/add?client_id=abcdef&key=segredo";
        var descriptor = (string)(method.Invoke(null, new object[] { url }) ?? string.Empty);

        Assert.Contains("client_id", descriptor, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("key", descriptor, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("abcdef", descriptor, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("segredo", descriptor, StringComparison.OrdinalIgnoreCase);
    }

    private static object Parse(string dump, string resourceId)
    {
        var method = typeof(SmartUiAutomationService).GetMethod(
            "ParseActivityViewBounds",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(
                typeof(SmartUiAutomationService).FullName,
                "ParseActivityViewBounds");

        return method.Invoke(null, new object[] { dump, resourceId })
            ?? throw new InvalidOperationException("O parser nao retornou um resultado.");
    }

    private static object GetTefLayout(int width, int height, string? profile)
    {
        var method = typeof(SmartUiAutomationService).GetMethod(
            "GetSmartTefLayout",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(
                typeof(SmartUiAutomationService).FullName,
                "GetSmartTefLayout");

        return method.Invoke(null, new object?[] { width, height, profile })
            ?? throw new InvalidOperationException("O layout TEF nao foi retornado.");
    }

    private static T Read<T>(object instance, string propertyName) =>
        (T)(instance.GetType().GetProperty(propertyName)?.GetValue(instance)
            ?? throw new MissingMemberException(instance.GetType().FullName, propertyName));
}
