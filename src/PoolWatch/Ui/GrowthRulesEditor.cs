using System.ComponentModel;
using System.ComponentModel.Design;
using System.Drawing.Design;

namespace PoolWatch.Ui;

/// <summary>
/// The list editor of the growth alerts in Settings, Advanced (<see cref="Core.Settings.GrowthRules"/>): WinForms' own
/// collection editor, which hides the box under its property grid that shows the selected value's description. This one
/// shows that box, and opens wide enough that each of an alert's two descriptions is whole in it (two lines under the
/// value's name, as in the dialog's own grid), and names the list in its title.
/// </summary>
internal sealed class GrowthRulesEditor() : CollectionEditor(typeof(List<Core.GrowthRule>))
{
    /// <summary>The editor's size at 96 dpi: wide enough for each description in two lines of its help box.</summary>
    internal static readonly Size LogicalSize = new(960, 520);

    /// <summary>Makes the grid show this editor for the growth alerts (the settings, in PoolWatch.Core, know no WinForms type).</summary>
    internal static void Register() =>
        TypeDescriptor.AddAttributes(typeof(List<Core.GrowthRule>), new EditorAttribute(typeof(GrowthRulesEditor), typeof(UITypeEditor)));

    protected override CollectionForm CreateCollectionForm()
    {
        var form = base.CreateCollectionForm();
        form.Text = "Growth alerts";
        form.Size = form.LogicalToDeviceUnits(LogicalSize);
        foreach (var grid in GridsIn(form)) grid.HelpVisible = true;
        return form;
    }

    /// <summary>The form the list editor opens (for the tests: what it shows, never shown).</summary>
    internal Form Form() => CreateCollectionForm();

    /// <summary>Every property grid in <paramref name="control"/> and in the controls inside it.</summary>
    internal static IEnumerable<PropertyGrid> GridsIn(Control control)
    {
        foreach (Control c in control.Controls)
        {
            if (c is PropertyGrid grid) yield return grid;
            foreach (var inner in GridsIn(c)) yield return inner;
        }
    }
}
