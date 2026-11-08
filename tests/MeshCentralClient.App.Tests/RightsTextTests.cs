using MeshCentralClient.App;
using MeshCentralClient.Core;
using Xunit;

/// <summary>
/// The rights a person ticks are the ones sent, each with its own text: a missing text would show its key, and a right
/// left out of the dialog could never be given.
/// Los permisos que marca una persona son los que se mandan, cada uno con su texto: un texto que falte enseñaría su
/// clave, y un permiso fuera del diálogo no se podría dar nunca.
/// </summary>
public class RightsTextTests
{
    [Fact]
    public void Every_right_in_the_dialog_has_its_text()
    {
        foreach (var (_, key) in RightsDialog.Allows.Concat(RightsDialog.Limits)) Assert.NotEqual(key, L.T(key));
    }

    [Fact]
    public void Each_right_appears_once_and_the_two_lists_do_not_overlap()
    {
        var all = RightsDialog.Allows.Concat(RightsDialog.Limits).Select(a => a.Right).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
        // Every single-bit right of the website's dialog, except the ones it never shows (registry, software list).
        // Todos los permisos de un bit del diálogo de la web, salvo los que nunca enseña (registro, lista de programas).
        var expected = Enum.GetValues<MeshRights>().Where(r => r is not (MeshRights.None or MeshRights.Full or MeshRights.NoRegistry or MeshRights.NoSoftware)).ToHashSet();
        Assert.Equal(expected, all.ToHashSet());
    }

    [Fact]
    public void The_summary_names_what_was_given()
    {
        Assert.Equal(L.T("RightFullShort"), RightsDialog.Summary(0xFFFFFFFF));
        Assert.Equal(L.T("RightNone"), RightsDialog.Summary(0));
        Assert.Equal($"{L.T("RightWake")}, {L.T("RightNoFiles")}", RightsDialog.Summary((long)(MeshRights.Wake | MeshRights.NoFiles)));
    }
}
