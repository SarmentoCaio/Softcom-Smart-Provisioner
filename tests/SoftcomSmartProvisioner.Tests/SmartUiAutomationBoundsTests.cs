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
