using System.Text.Json.Nodes;

namespace SFVaultBridge.Core;

/// <summary>Every bridge operation: JSON body in, interface call, JSON reply out.
/// Names and shapes match scripts/vault_bridge.py and the older vault_sdk wrappers.</summary>
public static class BridgeOps
{
    public const string NotSignedInMessage = "Sign in to Vault in Inventor (Vault tab, Log In).";

    public static void RegisterAll(Router router, IVaultSession vault, IStepImporter inventor, PathGuard guard,
                                   string bridgeVersion, Func<Func<JsonNode?>, JsonNode?> onUi,
                                   IDataCardHost? dataCard = null, Action<string>? log = null,
                                   IAttachPdfHost? attachPdf = null, IVendorPdfFetcher? pdfFetcher = null,
                                   IMaterialLibraryHost? materials = null)
    {
        void Reg(string name, Func<JsonObject, JsonNode?> op) => router.Register(name, body => onUi(() => op(body)));

        Reg("status", _ =>
        {
            var s = vault.Status();
            return new JsonObject
            {
                ["signed_in"] = s.SignedIn, ["user"] = s.User, ["vault"] = s.Vault, ["server"] = s.Server,
                ["inventor_version"] = inventor.InventorVersion, ["bridge_version"] = bridgeVersion, ["version"] = bridgeVersion,
            };
        });
        Reg("get_numbering_schemes", _ => new JsonObject
        {
            ["schemes"] = Arr(vault.GetNumberingSchemes().Select(s => new JsonObject { ["id"] = s.Id, ["name"] = s.Name, ["isDefault"] = s.IsDefault })),
        });
        Reg("generate_file_number", b => new JsonObject { ["number"] = vault.GenerateFileNumber(Args.Long(b, "schemeId")) });
        Reg("get_folder_by_path", b =>
        {
            var f = vault.GetFolderByPath(Args.Str(b, "path"));
            return f is null
                ? new JsonObject { ["found"] = false }
                : new JsonObject { ["found"] = true, ["id"] = f.Id, ["fullName"] = f.FullName, ["isLibrary"] = f.IsLibrary };
        });
        Reg("add_folder", b =>
        {
            var f = vault.AddFolder(Args.Str(b, "parentPath"), Args.Str(b, "name"));
            return new JsonObject { ["id"] = f.Id, ["fullName"] = f.FullName };
        });
        Reg("get_file_categories", _ => new JsonObject
        {
            ["categories"] = Arr(vault.GetFileCategories().Select(c => new JsonObject { ["id"] = c.Id, ["name"] = c.Name })),
        });
        Reg("add_file", b => FileJson(vault.AddFile(
            Args.Str(b, "folderPath"),
            guard.Check(Args.Str(b, "localPath"), "localPath"),
            Args.Str(b, "fileName"),
            Args.Longs(b, "attachmentFileIds", required: false),
            Args.OptStr(b, "comment"))));
        Reg("update_file_categories", b =>
        {
            var ids = Args.Longs(b, "masterIds");
            if (ids.Length == 0) throw new BridgeException("bad_request", "'masterIds' must not be empty.");
            return new JsonObject
            {
                ["updated"] = Arr(vault.UpdateFileCategories(ids, Args.Long(b, "categoryId"), Args.OptStr(b, "comment")).Select(FileJson)),
            };
        });
        Reg("import_step_as_part", b =>
        {
            var props = b["iproperties"] as JsonObject ?? new JsonObject();
            var outIpt = inventor.ImportStepAsPart(
                guard.Check(Args.Str(b, "stepPath"), "stepPath"),
                guard.Check(Args.Str(b, "outIpt"), "outIpt"),
                Args.StrMap(props, "design"),
                Args.StrMap(props, "custom"));
            return new JsonObject { ["outIpt"] = outIpt };
        });
        Reg("fix_data_card", b =>
        {
            var req = DataCardFix.ParseRequest(b);
            if (dataCard is null) throw new BridgeException("unavailable", "fix_data_card is not available in this build.");
            return DataCardFix.Run(req, dataCard, log);
        });
        Reg("attach_vendor_pdf", b =>
        {
            var req = AttachVendorPdf.ParseRequest(b, guard);
            if (attachPdf is null) throw new BridgeException("unavailable", "attach_vendor_pdf is not available in this build.");
            return AttachVendorPdf.Run(req, attachPdf, pdfFetcher, log);
        });
        Reg("add_library_material", b =>
        {
            var req = AddLibraryMaterial.ParseRequest(b);
            if (materials is null) throw new BridgeException("unavailable", "add_library_material is not available in this build.");
            return AddLibraryMaterial.Run(req, materials, log);
        });
    }

    private static JsonArray Arr(IEnumerable<JsonObject> items) => new(items.Select(i => (JsonNode?)i).ToArray());

    private static JsonObject FileJson(VaultFile f) =>
        new() { ["id"] = f.Id, ["masterId"] = f.MasterId, ["name"] = f.Name, ["checkedOut"] = f.CheckedOut };
}
