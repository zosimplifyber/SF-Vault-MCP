"""
Vault MCP Server
Exposes Autodesk Vault REST API operations as MCP tools via SSE transport.
Configuration is loaded from config.json and credentials are set automatically.
"""

import json
import logging
import re
import sys
from pathlib import Path
from typing import Any, Dict, List, Optional, Tuple

from mcp.server.fastmcp import FastMCP

import bom_purchasing
import vault_slim as slim
from pdf_watermark import apply_watermark
from vault_rest_api import VaultRestAPI

# scripts/ holds the release-workflow modules (compliance check + orchestrator).
# Import lazily inside the tools so a missing scripts/ folder never breaks
# server startup for the other tools.
_SCRIPTS_DIR = Path(__file__).resolve().parent / "scripts"
if _SCRIPTS_DIR.exists() and str(_SCRIPTS_DIR) not in sys.path:
    sys.path.insert(0, str(_SCRIPTS_DIR))

logger = logging.getLogger(__name__)


def _extract_collection(data: Any) -> List[Dict[str, Any]]:
    """Pull a list of records out of a Vault REST response payload.

    Vault v2 wraps collections in a few different keys depending on the endpoint
    (`results`, `items`, `data`, `value`); this normalizes them.
    """
    if data is None:
        return []
    if isinstance(data, list):
        return [r for r in data if isinstance(r, dict)]
    if isinstance(data, dict):
        for key in ("results", "items", "itemVersions", "data", "value", "records"):
            inner = data.get(key)
            if isinstance(inner, list):
                return [r for r in inner if isinstance(r, dict)]
        if data.get("id") or data.get("masterId"):
            return [data]
    return []


def _extract_id(record: Optional[Dict[str, Any]]) -> str:
    """Pull the canonical ID from an item / item-version / file / file-version record."""
    if not isinstance(record, dict):
        return ""
    for key in (
        "id",
        "itemVersionId",
        "fileVersionId",
        "masterId",
        "itemId",
        "fileId",
    ):
        v = record.get(key)
        if v:
            return str(v)
    return ""


def _pick_latest_version(item: Dict[str, Any]) -> Tuple[str, Optional[Dict[str, Any]]]:
    """If the item record embeds a latest-version reference, return (id, record)."""
    if not isinstance(item, dict):
        return "", None
    for key in ("latestItemVersion", "latestVersion", "latest"):
        v = item.get(key)
        if isinstance(v, dict):
            vid = _extract_id(v)
            if vid:
                return vid, v
    for key in ("latestItemVersionId", "latestVersionId"):
        v = item.get(key)
        if v:
            return str(v), None
    return "", None


_INVALID_FILENAME_CHARS = re.compile(r'[<>:"/\\|?*\x00-\x1f]')


def _safe_filename(name: str, used: set) -> str:
    """Sanitize a Vault file name for the local filesystem and de-duplicate
    against names already written in this run."""
    cleaned = _INVALID_FILENAME_CHARS.sub("_", name).strip(" .") or "file.pdf"
    if cleaned not in used:
        return cleaned
    stem = Path(cleaned).stem
    suffix = Path(cleaned).suffix or ".pdf"
    i = 2
    while True:
        candidate = f"{stem} ({i}){suffix}"
        if candidate not in used:
            return candidate
        i += 1


def _latest_by_revision(versions: List[Dict[str, Any]]) -> Optional[Dict[str, Any]]:
    """Pick the most recent version from a list — prefer revision number, fall back to ID."""
    if not versions:
        return None

    def sort_key(v: Dict[str, Any]):
        for k in ("revisionNumber", "revNumber", "versionNumber", "version"):
            n = v.get(k)
            if isinstance(n, (int, float)):
                return (1, n)
            if isinstance(n, str) and n.isdigit():
                return (1, int(n))
        rid = _extract_id(v)
        try:
            return (0, int(rid)) if rid else (0, 0)
        except ValueError:
            return (0, 0)

    return sorted(versions, key=sort_key)[-1]


def create_mcp_server(api: VaultRestAPI, vault_id: str) -> FastMCP:
    """
    Build and return a FastMCP instance wired to the provided VaultRestAPI client.
    vault_id is the resolved vault ID obtained after sign-in (used as the default
    vault for all vault-scoped calls).
    """

    mcp = FastMCP(
        name="vault-mcp",
        instructions=(
            "MCP server for Autodesk Vault. "
            "Authentication is handled automatically from config.json. "
            "Use the tools below to browse vaults, search files, read properties, and more."
        ),
    )

    # ------------------------------------------------------------------
    # Helper
    # ------------------------------------------------------------------

    def _resolved_vault(v: Optional[str]) -> str:
        """Return the caller-supplied vault ID, falling back to the config vault."""
        return (v or vault_id or "").strip()

    def _fmt(result: Dict[str, Any]) -> str:
        """Serialize an API result dict to a pretty JSON string."""
        return json.dumps(result, indent=2, default=str)

    def _view(result: Dict[str, Any], view, raw: bool = False) -> str:
        """Return the compact view of a successful call, or the raw envelope.

        Errors always pass through untouched so the status and message survive.
        """
        if raw or result.get("error"):
            return _fmt(result)
        return json.dumps(view(result.get("data")), ensure_ascii=False, default=str)

    # ------------------------------------------------------------------
    # Server information
    # ------------------------------------------------------------------

    @mcp.tool()
    async def vault_get_server_info() -> str:
        """
        Get Vault server information including product version and metadata.
        No parameters required.
        """
        result = await api.get_server_info()
        return _fmt(result)

    # ------------------------------------------------------------------
    # Authentication
    # ------------------------------------------------------------------

    @mcp.tool()
    async def vault_sign_in(username: str, password: str, database: str) -> str:
        """
        Authenticate with the Vault server and obtain a session token.
        Use this tool only when you need to sign in with different credentials
        than those in config.json. The server auto-signs-in on startup.

        Args:
            username: Vault username.
            password: Vault password.
            database: Vault database name (e.g. "Vault").
        """
        result = await api.create_session(database, username, password)
        if result["error"]:
            return _fmt(result)
        # Update vault_id from vaultInformation.id so subsequent tool calls use it
        nonlocal vault_id
        new_id = str(
            (result["data"].get("vaultInformation") or {}).get("id", "")
            or api._vault_id
            or vault_id
        )
        vault_id = new_id
        return _fmt(result)

    @mcp.tool()
    async def vault_sign_out(session_id: str) -> str:
        """
        Sign out and invalidate the current session.

        Args:
            session_id: The session / ticket ID to invalidate.
        """
        result = await api.delete_session(session_id)
        return _fmt(result)

    # ------------------------------------------------------------------
    # Vaults
    # ------------------------------------------------------------------

    @mcp.tool()
    async def vault_list_vaults() -> str:
        """
        List all vaults accessible with the current session.
        Returns vault names, IDs, and descriptions.
        """
        result = await api.get_vaults()
        return _view(result, slim.strip_noise)

    @mcp.tool()
    async def vault_get_vault(vault_id_param: str = "") -> str:
        """
        Get details for a specific vault.

        Args:
            vault_id_param: Vault ID. Leave empty to use the vault from config.json.
        """
        result = await api.get_vault_by_id(_resolved_vault(vault_id_param))
        return _fmt(result)

    # ------------------------------------------------------------------
    # Folders
    # ------------------------------------------------------------------

    @mcp.tool()
    async def vault_get_folder_contents(
        folder_id: str = "$",
        vault_id_param: str = "",
        query: str = "",
        search_sub_folders: bool = False,
        limit: int = 100,
        raw: bool = False,
    ) -> str:
        """
        List the contents of a vault folder (sub-folders and files).

        Returns {total, returned, results}; each result is a folder
        (name, path, folder_id) or a file (name, revision, state,
        file_version_id, file_id, checked_out_by).

        Args:
            folder_id: Folder ID. Use "$" or leave empty for the root folder.
            vault_id_param: Vault ID. Leave empty to use the vault from config.json.
            query: Optional keyword filter applied to folder contents.
            search_sub_folders: When True, include results from sub-folders.
            limit: Maximum number of results to return (default 100).
            raw: Return the unprocessed Vault response instead of the compact view.
        """
        result = await api.get_folder_contents(
            vault_id=_resolved_vault(vault_id_param),
            folder_id=folder_id or "$",
            query=query or None,
            search_sub_folders=search_sub_folders,
            limit=limit,
        )
        return _view(result, slim.collection, raw)

    @mcp.tool()
    async def vault_get_folder(folder_id: str, vault_id_param: str = "", raw: bool = False) -> str:
        """
        Get a folder's name, full path and sub-folder count.

        Args:
            folder_id: The folder ID to retrieve.
            vault_id_param: Vault ID. Leave empty to use the vault from config.json.
            raw: Return the unprocessed Vault response instead of the compact view.
        """
        result = await api.get_folder_by_id(_resolved_vault(vault_id_param), folder_id)
        return _view(result, slim.folder, raw)

    # ------------------------------------------------------------------
    # Files
    # ------------------------------------------------------------------

    @mcp.tool()
    async def vault_get_file(
        file_id: str,
        vault_id_param: str = "",
        released_only: bool = False,
        include_properties: bool = True,
        raw: bool = False,
    ) -> str:
        """
        Get the latest version of a file: name, revision, state, folder,
        who has it checked out, and its properties as {name: value}.

        Args:
            file_id: Master file ID or any file-version ID of the file.
            vault_id_param: Vault ID. Leave empty to use the vault from config.json.
            released_only: When True, return the latest released version instead.
            include_properties: Include the property values (default True).
            raw: Return the unprocessed Vault response instead of the compact view.
        """
        result = await api.get_file_by_id(
            vault_id=_resolved_vault(vault_id_param),
            file_id=file_id,
            released_only=released_only,
        )
        return _view(
            result,
            lambda d: slim.file_version(
                (d or {}).get("fileVersion") or {}, with_properties=include_properties
            ),
            raw,
        )

    @mcp.tool()
    async def vault_get_file_versions(
        file_id: str,
        vault_id_param: str = "",
        limit: int = 50,
        raw: bool = False,
    ) -> str:
        """
        List the version history of a file (version, revision, state, date).

        Args:
            file_id: The master file ID (the "file_id" field of other tools).
            vault_id_param: Vault ID. Leave empty to use the vault from config.json.
            limit: Maximum number of versions to return.
            raw: Return the unprocessed Vault response instead of the compact view.
        """
        result = await api.get_file_versions(
            vault_id=_resolved_vault(vault_id_param), file_id=file_id, limit=limit
        )
        return _view(result, slim.collection, raw)

    @mcp.tool()
    async def vault_get_file_download_url(
        file_id: str, file_version_id: str = "", vault_id_param: str = ""
    ) -> str:
        """
        Get a time-limited, signed download link for a file. The link needs
        no Vault session, so it can be fetched with any HTTP client on the
        network that reaches the Vault server.

        Args:
            file_id: Master file ID or file-version ID; the latest version is used.
            file_version_id: A specific version to download instead of the latest.
            vault_id_param: Vault ID. Leave empty to use the vault from config.json.
        """
        vault = _resolved_vault(vault_id_param)
        name = ""
        fv_id = file_version_id.strip()
        if not fv_id:
            file_resp = await api.get_file_by_id(vault_id=vault, file_id=file_id)
            if file_resp.get("error"):
                return _fmt(file_resp)
            fv = (file_resp.get("data") or {}).get("fileVersion") or {}
            fv_id, name = str(fv.get("id") or ""), fv.get("name") or ""
        if not fv_id:
            return _fmt({"error": True, "message": f"Could not resolve a file version from file_id={file_id}"})
        result = await api.get_file_version_signed_url(vault_id=vault, file_version_id=fv_id)
        return _view(
            result,
            lambda d: {"name": name, "file_version_id": fv_id, "download_url": (d or {}).get("url")},
        )

    @mcp.tool()
    async def vault_get_file_where_used(
        file_id: str, vault_id_param: str = "", limit: int = 100, raw: bool = False
    ) -> str:
        """
        CAD where-used: the assemblies and drawings that reference a file.
        The file-side counterpart of vault_get_item_parents.

        Args:
            file_id: Master file ID or file-version ID; the latest version is used.
            vault_id_param: Vault ID. Leave empty to use the vault from config.json.
            limit: Maximum number of parents to return.
            raw: Return the unprocessed Vault response instead of the compact view.
        """
        vault = _resolved_vault(vault_id_param)
        file_resp = await api.get_file_by_id(vault_id=vault, file_id=file_id)
        if file_resp.get("error"):
            return _fmt(file_resp)
        fv = (file_resp.get("data") or {}).get("fileVersion") or {}
        result = await api.get_file_parents(
            vault_id=vault, file_version_id=str(fv.get("id") or file_id), limit=limit
        )
        return _view(
            result,
            lambda d: {"file": slim.file_version(fv), **slim.file_parents(d or {})},
            raw,
        )

    @mcp.tool()
    async def vault_get_file_items(
        file_id: str, vault_id_param: str = "", raw: bool = False
    ) -> str:
        """
        The engineering item(s) a CAD file is linked to, e.g. CD-001621.iam ->
        SF-001942. Use it to go from a drawing or model to its part number, BOM
        and purchasing data.

        Args:
            file_id: Master file ID or file-version ID; the latest version is used.
            vault_id_param: Vault ID. Leave empty to use the vault from config.json.
            raw: Return the unprocessed Vault response instead of the compact view.
        """
        vault = _resolved_vault(vault_id_param)
        file_resp = await api.get_file_by_id(vault_id=vault, file_id=file_id)
        if file_resp.get("error"):
            return _fmt(file_resp)
        fv = (file_resp.get("data") or {}).get("fileVersion") or {}
        result = await api.get_file_item_versions(
            vault_id=vault, file_version_id=str(fv.get("id") or file_id)
        )
        return _view(
            result,
            lambda d: {"file": slim.file_version(fv), **slim.collection(d)},
            raw,
        )

    # ------------------------------------------------------------------
    # Search
    # ------------------------------------------------------------------

    @mcp.tool()
    async def vault_search_files(
        query: str,
        vault_id_param: str = "",
        search_content: bool = False,
        search_sub_folders: bool = True,
        released_files_only: bool = False,
        latest_only: bool = True,
        limit: int = 50,
        raw: bool = False,
    ) -> str:
        """
        Search for files in the vault using a keyword query.

        Returns {total, returned, results}; each file has name, revision, state,
        folder path, file_version_id and file_id.

        Args:
            query: Keyword(s) to search for (e.g. "pump assembly" or "CD-001621").
            vault_id_param: Vault ID. Leave empty to use the vault from config.json.
            search_content: When True, also search inside file content.
            search_sub_folders: When True, include sub-folders in the search.
            released_files_only: When True, only return released files.
            latest_only: When True, return only the latest version of each file.
            limit: Maximum number of results to return.
            raw: Return the unprocessed Vault response instead of the compact view.
        """
        result = await api.search_files(
            vault_id=_resolved_vault(vault_id_param),
            query=query,
            search_content=search_content,
            search_sub_folders=search_sub_folders,
            released_files_only=released_files_only,
            latest_only=latest_only,
            limit=limit,
        )
        return _view(result, slim.collection, raw)

    _SEARCH_OPERATORS = {
        "Contains", "DoesNotContain", "IsExactly", "IsEmpty", "IsNotEmpty",
        "GreaterThan", "GreaterThanOrEqualTo", "LessThan", "LessThanOrEqualTo",
        "NotEqualTo",
    }

    @mcp.tool()
    async def vault_advanced_search(
        conditions_json: str,
        entity_types: str = "",
        folder_id: str = "",
        released_only: bool = False,
        latest_only: bool = True,
        vault_id_param: str = "",
        limit: int = 50,
        raw: bool = False,
    ) -> str:
        """
        Property search: find files, items, folders or change orders whose
        properties match every condition (AND).

        Args:
            conditions_json: JSON list of conditions, each
                {"property": <display name, system name or definition id>,
                 "operator": <operator>, "value": <text>}.
                Operators: Contains, DoesNotContain, IsExactly, IsEmpty,
                IsNotEmpty, GreaterThan, GreaterThanOrEqualTo, LessThan,
                LessThanOrEqualTo, NotEqualTo.
                Example: '[{"property": "State", "operator": "IsExactly",
                "value": "Work in Progress"}, {"property": "Category Name",
                "operator": "IsExactly", "value": "Assembly - Engineering"}]'
            entity_types: Comma list of File, Item, Folder, ChangeOrder.
                Empty searches all of them.
            folder_id: Limit the search to this folder (and its sub-folders).
            released_only: Only released files and items.
            latest_only: Only the latest version of each file/item (default True).
            vault_id_param: Vault ID. Leave empty to use the vault from config.json.
            limit: Maximum number of results to return.
            raw: Return the unprocessed Vault response instead of the compact view.
        """
        vault = _resolved_vault(vault_id_param)
        try:
            conditions = json.loads(conditions_json)
        except json.JSONDecodeError as exc:
            return _fmt({"error": True, "message": f"Invalid JSON in conditions_json: {exc}"})
        if isinstance(conditions, dict):
            conditions = [conditions]
        if not isinstance(conditions, list) or not conditions:
            return _fmt({"error": True, "message": "conditions_json must be a non-empty JSON list."})

        defs = await api.get_property_definitions(vault_id=vault, limit=1000)
        if defs.get("error"):
            return _fmt({"step": "get_property_definitions", "result": defs})
        by_name: Dict[str, str] = {}
        for d in _extract_collection(defs.get("data")):
            for key in (d.get("id"), d.get("displayName"), d.get("systemName")):
                if key:
                    by_name[str(key).strip().lower()] = str(d.get("id"))

        criteria = []
        for c in conditions:
            prop = str(c.get("property", "")).strip()
            op = str(c.get("operator", "")).strip()
            prop_id = by_name.get(prop.lower())
            if not prop_id:
                return _fmt({"error": True, "message": f"Unknown property {prop!r}. See vault_list_property_definitions."})
            if op not in _SEARCH_OPERATORS:
                return _fmt({"error": True, "message": f"Unknown operator {op!r}. Use one of {sorted(_SEARCH_OPERATORS)}."})
            criteria.append({
                "propertyDefinitionUrl": f"/AutodeskDM/Services/api/vault/v2/vaults/{vault}/property-definitions/{prop_id}",
                "operator": op,
                "searchString": "" if c.get("value") is None else str(c.get("value")),
            })

        body: Dict[str, Any] = {"searchCriterias": criteria}
        types = [t.strip() for t in entity_types.split(",") if t.strip()]
        if types:
            body["entityTypesToSearch"] = types
        if folder_id:
            body["foldersToSearch"] = [f"/AutodeskDM/Services/api/vault/v2/vaults/{vault}/folders/{folder_id}"]

        result = await api.advanced_search(
            vault_id=vault,
            body=body,
            limit=limit,
            released_files_only=released_only,
            released_items_only=released_only,
            latest_only=latest_only,
        )
        return _view(result, slim.collection, raw)


    # ------------------------------------------------------------------
    # Accounts
    # ------------------------------------------------------------------

    @mcp.tool()
    async def vault_list_groups(limit: int = 100) -> str:
        """
        List all groups defined in the vault.

        Args:
            limit: Maximum number of groups to return.
        """
        result = await api.get_groups(limit=limit)
        return _view(result, slim.strip_noise)

    @mcp.tool()
    async def vault_get_group(group_id: str) -> str:
        """
        Get details for a specific group.

        Args:
            group_id: The group ID.
        """
        result = await api.get_group_by_id(group_id)
        return _view(result, slim.strip_noise)

    @mcp.tool()
    async def vault_list_users(limit: int = 100) -> str:
        """
        List all users in the vault.

        Args:
            limit: Maximum number of users to return.
        """
        result = await api.get_users(limit=limit)
        return _view(result, slim.strip_noise)

    @mcp.tool()
    async def vault_get_user(user_id: str) -> str:
        """
        Get details for a specific user.

        Args:
            user_id: The user ID.
        """
        result = await api.get_user_by_id(user_id)
        return _view(result, slim.strip_noise)

    # ------------------------------------------------------------------
    # Property definitions
    # ------------------------------------------------------------------

    @mcp.tool()
    async def vault_list_property_definitions(
        vault_id_param: str = "", limit: int = 200
    ) -> str:
        """
        List all user-defined property definitions in the vault.

        Args:
            vault_id_param: Vault ID. Leave empty to use the vault from config.json.
            limit: Maximum number of definitions to return.
        """
        result = await api.get_property_definitions(
            vault_id=_resolved_vault(vault_id_param), limit=limit
        )
        return _view(result, slim.strip_noise)

    @mcp.tool()
    async def vault_get_property_definition(
        prop_def_id: str, vault_id_param: str = ""
    ) -> str:
        """
        Get a specific property definition by its ID.

        Args:
            prop_def_id: The property definition ID.
            vault_id_param: Vault ID. Leave empty to use the vault from config.json.
        """
        result = await api.get_property_definition_by_id(
            vault_id=_resolved_vault(vault_id_param), prop_def_id=prop_def_id
        )
        return _view(result, slim.strip_noise)

    # ------------------------------------------------------------------
    # Items (Engineering)
    # ------------------------------------------------------------------

    @mcp.tool()
    async def vault_search_items(
        query: str,
        vault_id_param: str = "",
        limit: int = 50,
        raw: bool = False,
    ) -> str:
        """
        Search for engineering items (part numbers) in the vault.

        Returns {total, returned, results}; each item has number, title,
        description, revision, state, category, item_version_id and item_id.

        Args:
            query: Keyword(s) to search for.
            vault_id_param: Vault ID. Leave empty to use the vault from config.json.
            limit: Maximum number of results to return.
            raw: Return the unprocessed Vault response instead of the compact view.
        """
        result = await api.search_items(
            vault_id=_resolved_vault(vault_id_param), query=query, limit=limit
        )
        return _view(result, slim.collection, raw)

    @mcp.tool()
    async def vault_get_item(
        item_id: str,
        vault_id_param: str = "",
        include_properties: bool = True,
        raw: bool = False,
    ) -> str:
        """
        Get an engineering item's latest version, with its properties as
        {name: value}.

        Args:
            item_id: The master item ID.
            vault_id_param: Vault ID. Leave empty to use the vault from config.json.
            include_properties: Include the property values (default True).
            raw: Return the unprocessed Vault response instead of the compact view.
        """
        result = await api.get_item_by_id(
            vault_id=_resolved_vault(vault_id_param), item_id=item_id
        )
        return _view(
            result, lambda d: slim.item(d or {}, with_properties=include_properties), raw
        )

    @mcp.tool()
    async def vault_get_item_version_history(
        item_id: str, vault_id_param: str = "", limit: int = 50, raw: bool = False
    ) -> str:
        """
        Get the full version history for a master engineering item.

        Args:
            item_id: The master item ID.
            vault_id_param: Vault ID. Leave empty to use the vault from config.json.
            limit: Maximum number of versions to return.
            raw: Return the unprocessed Vault response instead of the compact view.
        """
        result = await api.get_item_version_history(
            vault_id=_resolved_vault(vault_id_param), item_id=item_id, limit=limit
        )
        return _view(result, slim.collection, raw)

    @mcp.tool()
    async def vault_get_item_change_orders(
        item_id: str, vault_id_param: str = "", limit: int = 100, raw: bool = False
    ) -> str:
        """
        Get change orders linked to a specific item.

        Args:
            item_id: The master item ID.
            vault_id_param: Vault ID. Leave empty to use the vault from config.json.
            limit: Maximum number of change orders to return.
            raw: Return the unprocessed Vault response instead of the compact view.
        """
        result = await api.get_item_change_orders(
            vault_id=_resolved_vault(vault_id_param), item_id=item_id, limit=limit
        )
        return _view(result, slim.collection, raw)

    # ------------------------------------------------------------------
    # Item versions & Bill of Materials
    # ------------------------------------------------------------------

    @mcp.tool()
    async def vault_list_item_versions(
        query: str = "", vault_id_param: str = "", limit: int = 100, raw: bool = False
    ) -> str:
        """
        List item versions in the vault, optionally filtered by a keyword query.

        Args:
            query: Optional keyword filter (e.g. a part number). Leave empty to list all.
            vault_id_param: Vault ID. Leave empty to use the vault from config.json.
            limit: Maximum number of results to return.
            raw: Return the unprocessed Vault response instead of the compact view.
        """
        result = await api.list_item_versions(
            vault_id=_resolved_vault(vault_id_param),
            query=query or None,
            limit=limit,
        )
        return _view(result, slim.collection, raw)

    @mcp.tool()
    async def vault_get_item_version(
        item_version_id: str,
        vault_id_param: str = "",
        include_properties: bool = True,
        raw: bool = False,
    ) -> str:
        """
        Get a specific item version, with its properties as {name: value}.

        Args:
            item_version_id: The item-version ID (different from the master item ID).
            vault_id_param: Vault ID. Leave empty to use the vault from config.json.
            include_properties: Include the property values (default True).
            raw: Return the unprocessed Vault response instead of the compact view.
        """
        result = await api.get_item_version_by_id(
            vault_id=_resolved_vault(vault_id_param), item_version_id=item_version_id
        )
        return _view(
            result,
            lambda d: slim.item_version(d or {}, with_properties=include_properties),
            raw,
        )

    @mcp.tool()
    async def vault_get_item_bom(
        item_version_id: str, vault_id_param: str = "", limit: int = 200, raw: bool = False
    ) -> str:
        """
        Get the Bill of Materials for an item version.

        Returns {assembly, row_count, rows}. Rows are depth-first with a dotted
        "row" number ("1", "1.2") like Inventor's structured BOM; qty is per
        parent. If you only have a part number, use vault_get_bom_by_part_number.

        Args:
            item_version_id: The item-version ID whose BOM you want.
            vault_id_param: Vault ID. Leave empty to use the vault from config.json.
            limit: Maximum number of BOM rows to return.
            raw: Return the unprocessed Vault response instead of the compact view.
        """
        result = await api.get_item_bom(
            vault_id=_resolved_vault(vault_id_param),
            item_version_id=item_version_id,
            limit=limit,
        )
        return _view(result, lambda d: slim.item_bom(d or {}, item_version_id), raw)

    @mcp.tool()
    async def vault_get_item_parents(
        item_version_id: str, vault_id_param: str = "", limit: int = 100, raw: bool = False
    ) -> str:
        """
        Where-used for an item version: the assemblies that use it directly
        (with quantity), plus the higher-level assemblies above those.

        Args:
            item_version_id: The item-version ID.
            vault_id_param: Vault ID. Leave empty to use the vault from config.json.
            limit: Maximum number of parents to return.
            raw: Return the unprocessed Vault response instead of the compact view.
        """
        result = await api.get_item_parents(
            vault_id=_resolved_vault(vault_id_param),
            item_version_id=item_version_id,
            limit=limit,
        )
        return _view(result, lambda d: slim.item_parents(d or {}, item_version_id), raw)

    @mcp.tool()
    async def vault_get_item_associated_files(
        item_version_id: str, vault_id_param: str = "", limit: int = 100, raw: bool = False
    ) -> str:
        """
        Get the files linked to an item version (e.g. its primary .iam/.ipt
        and drawings), with the association type.

        Args:
            item_version_id: The item-version ID.
            vault_id_param: Vault ID. Leave empty to use the vault from config.json.
            limit: Maximum number of associated files to return.
            raw: Return the unprocessed Vault response instead of the compact view.
        """
        result = await api.get_item_associated_files(
            vault_id=_resolved_vault(vault_id_param),
            item_version_id=item_version_id,
            limit=limit,
        )
        return _view(result, slim.associated_files, raw)

    @mcp.tool()
    async def vault_list_change_orders(
        open_only: bool = True, vault_id_param: str = "", limit: int = 100, raw: bool = False
    ) -> str:
        """
        List change orders (ECOs): number, title, state, due date, change_order_id.

        Args:
            open_only: Only open change orders (default True).
            vault_id_param: Vault ID. Leave empty to use the vault from config.json.
            limit: Maximum number of change orders to return.
            raw: Return the unprocessed Vault response instead of the compact view.
        """
        result = await api.list_change_orders(
            vault_id=_resolved_vault(vault_id_param), open_only=open_only, limit=limit
        )
        return _view(result, slim.collection, raw)

    @mcp.tool()
    async def vault_get_change_order(
        change_order_id: str, vault_id_param: str = "", raw: bool = False
    ) -> str:
        """
        Get one change order plus the items and files attached to it.

        Args:
            change_order_id: The change order ID (from vault_list_change_orders
                or vault_get_item_change_orders).
            vault_id_param: Vault ID. Leave empty to use the vault from config.json.
            raw: Return the unprocessed Vault response instead of the compact view.
        """
        vault = _resolved_vault(vault_id_param)
        co = await api.get_change_order(vault_id=vault, change_order_id=change_order_id)
        if co.get("error"):
            return _fmt(co)
        entities = await api.get_change_order_entities(vault_id=vault, change_order_id=change_order_id)
        if raw:
            return _fmt({"change_order": co, "associated_entities": entities})
        out: Dict[str, Any] = {"change_order": slim.change_order(co.get("data") or {}, with_properties=True)}
        if entities.get("error"):
            out["associated_entities_error"] = entities
        else:
            out["associated"] = slim.collection(entities.get("data"))
        return json.dumps(out, ensure_ascii=False, default=str)

    def _best_match(records: List[Dict[str, Any]], part_number: str, key) -> Tuple[Dict[str, Any], List[str]]:
        """Prefer an exact part-number match over Vault's keyword ranking."""
        notes: List[str] = []
        target = part_number.strip().lower()
        exact = [r for r in records if key(r).strip().lower() == target]
        if exact:
            chosen = exact[0]
        else:
            chosen = records[0]
            notes.append(f"No exact match for '{part_number}'; using closest result '{key(chosen)}'.")
        if len(records) > 1 and not exact:
            others = ", ".join(key(r) for r in records[1:6])
            notes.append(f"{len(records)} matches; others: {others}.")
        return chosen, notes

    async def _resolve_item(vault: str, part_number: str) -> Dict[str, Any]:
        """Find the item for a part number and its latest item-version ID.

        Returns {master, item_version_id, item_version, notes} or {error: <payload>}.
        """
        search = await api.search_items(vault_id=vault, query=part_number, limit=10)
        if search["error"]:
            return {"error": {"step": "search_items", "result": search}}
        items = _extract_collection(search.get("data"))
        if not items:
            return {"error": {"step": "search_items", "message": f"No items found matching '{part_number}'."}}
        master, notes = _best_match(items, part_number, lambda r: str((r.get("itemVersion") or {}).get("number") or ""))
        item_id = _extract_id(master)
        if not item_id:
            return {"error": {"step": "extract_item_id", "message": "Could not determine the master item ID.", "matched_item": master}}

        item_version_id, item_version = _pick_latest_version(master)
        if not item_version_id and isinstance(master.get("itemVersion"), dict):
            item_version = master["itemVersion"]
            item_version_id = _extract_id(item_version)
        if not item_version_id:
            history = await api.get_item_version_history(vault_id=vault, item_id=item_id, limit=50)
            if history["error"]:
                return {"error": {"step": "get_item_version_history", "result": history}}
            item_version = _latest_by_revision(_extract_collection(history.get("data")))
            item_version_id = _extract_id(item_version)
        if not item_version_id:
            return {"error": {"step": "resolve_item_version_id", "message": "Could not determine an item-version ID.", "matched_item": master}}
        return {"master": master, "item_version_id": item_version_id, "item_version": item_version, "notes": notes}

    @mcp.tool()
    async def vault_get_cad_bom_by_part_number(
        part_number: str, vault_id_param: str = "", limit: int = 200, raw: bool = False
    ) -> str:
        """
        CAD BOM in one call: find the CAD file by part number / file name and
        list the files it references (one level deep), with association type.

        This is the assembly structure as modeled in CAD (.iam -> child files),
        which can differ from the engineering item BOM. For the item BOM use
        vault_get_bom_by_part_number.

        Args:
            part_number: CD number or file name, e.g. "CD-001621" or "CD-001621.iam".
                An exact file-name match wins; assemblies beat parts and drawings.
            vault_id_param: Vault ID. Leave empty to use the vault from config.json.
            limit: Maximum number of BOM rows to return.
            raw: Return the unprocessed Vault response instead of the compact view.
        """
        vault = _resolved_vault(vault_id_param)
        search = await api.search_files(
            vault_id=vault, query=part_number, search_sub_folders=True, latest_only=True, limit=20
        )
        if search["error"]:
            return _fmt({"step": "search_files", "result": search})
        files = _extract_collection(search.get("data"))
        if not files:
            return _fmt({"step": "search_files", "message": f"No files found matching '{part_number}'."})

        target = part_number.strip().lower()
        rank = {".iam": 0, ".ipt": 1, ".idw": 2, ".dwg": 3}

        def score(f: Dict[str, Any]):
            name = str(f.get("name") or "").lower()
            stem, ext = (name.rsplit(".", 1)[0], "." + name.rsplit(".", 1)[1]) if "." in name else (name, "")
            exact = name == target or stem == target
            return (0 if exact else 1, rank.get(ext, 9))

        matched = sorted(files, key=score)[0]
        notes: List[str] = []
        if score(matched)[0]:
            notes.append(f"No exact file-name match for '{part_number}'; using '{matched.get('name')}'.")
        file_version_id, _ = _pick_latest_version(matched)
        file_version_id = file_version_id or _extract_id(matched)
        if not file_version_id:
            return _fmt({"step": "extract_file_version_id", "message": "Could not determine the file-version ID.", "matched_file": matched})

        bom = await api.get_file_uses(vault_id=vault, file_version_id=file_version_id, limit=limit)
        if raw or bom.get("error"):
            return _fmt({"matched_file": matched, "file_version_id": file_version_id, "bom": bom, "notes": notes})
        uses = slim.file_uses(bom.get("data") or {})
        out = {"file": slim.file_version(matched), "children": uses["children"]}
        if notes:
            out["notes"] = notes
        return json.dumps(out, ensure_ascii=False, default=str)

    @mcp.tool()
    async def vault_get_bom_by_part_number(
        part_number: str, vault_id_param: str = "", limit: int = 200, raw: bool = False
    ) -> str:
        """
        Item BOM in one call: find the item by part number, take its latest
        version, and return its Bill of Materials.

        Returns {assembly, row_count, rows, notes}. Rows are depth-first with a
        dotted "row" number ("1", "1.2"), qty per parent, plus number, title,
        description, revision, state and item_version_id. An exact part-number
        match always wins over Vault's keyword ranking.

        Args:
            part_number: The part number, e.g. "SF-001942".
            vault_id_param: Vault ID. Leave empty to use the vault from config.json.
            limit: Maximum number of BOM rows to return.
            raw: Return the unprocessed Vault response instead of the compact view.
        """
        vault = _resolved_vault(vault_id_param)
        found = await _resolve_item(vault, part_number)
        if "error" in found:
            return _fmt(found["error"])
        bom = await api.get_item_bom(
            vault_id=vault, item_version_id=found["item_version_id"], limit=limit
        )
        if raw or bom.get("error"):
            return _fmt({
                "matched_item": found["master"],
                "item_version": found["item_version"],
                "item_version_id": found["item_version_id"],
                "bom": bom,
                "notes": found["notes"],
            })
        out = slim.item_bom(bom.get("data") or {}, found["item_version_id"])
        if found["notes"]:
            out["notes"] = found["notes"]
        return json.dumps(out, ensure_ascii=False, default=str)

    # ------------------------------------------------------------------
    # Lifecycle & Categories
    # ------------------------------------------------------------------

    @mcp.tool()
    async def vault_list_lifecycle_definitions(vault_id_param: str = "") -> str:
        """
        List all lifecycle definitions configured in the vault.

        Args:
            vault_id_param: Vault ID. Leave empty to use the vault from config.json.
        """
        result = await api.get_lifecycle_definitions(_resolved_vault(vault_id_param))
        return _view(result, slim.strip_noise)



    # ------------------------------------------------------------------
    # Jobs (Vault Job Queue)
    # ------------------------------------------------------------------

    @mcp.tool()
    async def vault_get_job_queue_enabled(vault_id_param: str = "") -> str:
        """
        Check whether the Vault job queue is enabled.

        If the queue is disabled, jobs submitted via vault_submit_job will sit
        unprocessed until a Job Processor agent comes online and the queue is
        re-enabled. Returns a boolean wrapped in the standard response envelope.

        Args:
            vault_id_param: Vault ID. Leave empty to use the vault from config.json.
        """
        result = await api.get_job_queue_enabled(_resolved_vault(vault_id_param))
        return _fmt(result)

    @mcp.tool()
    async def vault_submit_job(
        job_type: str,
        params_json: str = "{}",
        description: str = "",
        priority: int = 0,
        vault_id_param: str = "",
    ) -> str:
        """
        Submit a job to the Vault job queue. Picked up by the next Job Processor
        that handles the type. Capture the returned job id and poll vault_get_job
        to track status (Ready / Running / Success / Failure).

        CRITICAL — keys inside params_json must be PascalCase. The REST endpoint
        preserves the casing you send into the queue, but the JP handler ctors
        do case-sensitive lookups against PascalCase names. The JSON response
        echoes keys as camelCase (serialization quirk) — don't be misled. Sending
        camelCase keys silently fails with "Job param error" inside JP.

        Canonical JobType strings (PascalCase, dotted) and their required params,
        verified against a JP that successfully publishes from Vault Explorer:

          Autodesk.Vault.PDF.Create.idw    {"FileVersionId": "<id>", "UpdateViewOption": "False"}
          Autodesk.Vault.PDF.Create.dwg    {"FileVersionId": "<id>", "UpdateViewOption": "False"}
          Autodesk.Vault.DWF.Create.iam    {"FileVersionId": "<id>"}
          Autodesk.Vault.DWF.Create.idw    {"FileVersionId": "<id>"}
          Autodesk.Vault.DWF.Create.ipt    {"FileVersionId": "<id>"}
          Autodesk.Vault.DWF.Create.ipn    {"FileVersionId": "<id>"}
          Autodesk.Vault.DWF.Create.dwg    {"FileVersionId": "<id>"}
          Autodesk.Vault.DXF.Create.idw    {"FileVersionId": "<id>"}
          Autodesk.Vault.DXF.Create.dwg    {"FileVersionId": "<id>"}
          Autodesk.Vault.DXF.Create.ipt    {"FileVersionId": "<id>"}
          Autodesk.Vault.STEP.Create.iam   {"FileVersionId": "<id>", "UpdatePdfOption": "False", "UpdateViewOption": "False"}
          Autodesk.Vault.STEP.Create.ipt   {"FileVersionId": "<id>", "UpdatePdfOption": "False", "UpdateViewOption": "False"}
          Autodesk.Vault.SyncProperties    {"FileVersionId": "<id>"} or {"FileVersionIds": "id1,id2,..."}
          Autodesk.Vault.UpdateRevisionBlock.idw  {"FileVersionId": "<id>"}
          Autodesk.Vault.UpdateRevisionBlock.dwg  {"FileVersionId": "<id>"}
          Autodesk.Vault.ExtractBOM.Inventor      {"FileVersionId": "<id>"}

        The full list of registered job types lives in the JP's
        JobProcessor.exe.config under <connectivityExplorer><jobHandlers>. An
        unknown JobType is accepted by the REST endpoint but no JP claims it —
        the job sits at status=Ready forever (or is swept).

        FileVersionId is the version-specific id, not the master id. Resolve via
        vault_get_file (returns the latest version's id) or vault_search_files.

        For PDF specifically, prefer vault_publish_pdf_from_drawing — it
        auto-resolves latest version and includes UpdateViewOption.

        Args:
            job_type: PascalCase dotted job type, e.g. "Autodesk.Vault.PDF.Create.idw".
            params_json: JSON object with PascalCase keys, e.g.
                '{"FileVersionId": "112793", "UpdateViewOption": "False"}'.
            description: Optional human-readable description.
            priority: Optional priority (1 = highest). 0 means leave unset.
            vault_id_param: Vault ID. Leave empty to use the vault from config.json.
        """
        try:
            params = json.loads(params_json) if params_json else {}
        except json.JSONDecodeError as exc:
            return json.dumps({
                "error": True,
                "message": f"Invalid JSON in params_json: {exc}",
            })

        if not isinstance(params, dict):
            return json.dumps({
                "error": True,
                "message": "params_json must be a JSON object (dict).",
            })

        result = await api.submit_job(
            vault_id=_resolved_vault(vault_id_param),
            job_type=job_type,
            params={str(k): str(v) for k, v in params.items()},
            description=description or None,
            priority=priority if priority > 0 else None,
        )
        return _fmt(result)

    @mcp.tool()
    async def vault_publish_pdf_from_drawing(
        file_id: str,
        update_view_option: bool = False,
        description: str = "",
        priority: int = 10,
        vault_id_param: str = "",
    ) -> str:
        """
        Queue a PDF Create job for an Inventor (.idw) or AutoCAD (.dwg) drawing.

        Resolves the latest version of the given file, picks the right JobType
        based on the file extension, and submits with PascalCase params. Use
        this instead of vault_submit_job for PDF publishing — it gets the param
        casing, JobType, and version-id resolution right automatically.

        Args:
            file_id: File master id OR file version id of an .idw or .dwg.
                If a master id is given, the latest version is resolved and used.
            update_view_option: Whether the publish job should update the
                Inventor view option. Mirrors Vault Explorer's default (False).
            description: Optional description (auto-generated from file name if blank).
            priority: Job priority (1=highest, 10 typical default).
            vault_id_param: Vault ID. Leave empty to use config.json's vault.
        """
        vault_id = _resolved_vault(vault_id_param)

        # Resolve to the latest file version. The file endpoint accepts either
        # a master id or a version id and returns the same fileVersion shape.
        file_resp = await api.get_file_by_id(vault_id=vault_id, file_id=file_id)
        if file_resp.get("error"):
            return _fmt(file_resp)

        fv = (file_resp.get("data") or {}).get("fileVersion") or {}
        file_version_id = str(fv.get("id") or "")
        file_name = fv.get("name") or ""
        if not file_version_id:
            return json.dumps({
                "error": True,
                "message": f"Could not resolve a file version from file_id={file_id}",
            })

        ext = file_name.rsplit(".", 1)[-1].lower() if "." in file_name else ""
        if ext == "idw":
            job_type = "Autodesk.Vault.PDF.Create.idw"
        elif ext == "dwg":
            job_type = "Autodesk.Vault.PDF.Create.dwg"
        else:
            return json.dumps({
                "error": True,
                "message": (
                    f"PDF publish only supports .idw and .dwg drawings. "
                    f"File {file_name!r} has extension {ext!r}."
                ),
            })

        result = await api.submit_job(
            vault_id=vault_id,
            job_type=job_type,
            params={
                "FileVersionId": file_version_id,
                "UpdateViewOption": "True" if update_view_option else "False",
            },
            description=description or f"PDF Create: {file_name}",
            priority=priority,
        )
        return _fmt(result)

    @mcp.tool()
    async def vault_publish_step_from_model(
        file_id: str,
        description: str = "",
        priority: int = 10,
        vault_id_param: str = "",
    ) -> str:
        """
        Queue a STEP Create job for an Inventor part (.ipt) or assembly (.iam).

        Resolves the latest version of the given file, picks STEP.Create.ipt or
        STEP.Create.iam based on the extension, and submits with the param
        shape that Vault Explorer's GUI uses. Use this instead of
        vault_submit_job for STEP publishing — it gets all the cross-format
        toggles right (the JP ctor reads UpdatePdfOption AND UpdateViewOption
        despite the name; UpdateStpOption is NOT used).

        Args:
            file_id: File master id OR file version id of an .ipt or .iam.
                Latest version is resolved if a master id is given.
            description: Optional description (auto-generated from file name).
            priority: Job priority (1=highest, 10 typical default).
            vault_id_param: Vault ID. Leave empty to use config.json's vault.
        """
        vault_id = _resolved_vault(vault_id_param)

        file_resp = await api.get_file_by_id(vault_id=vault_id, file_id=file_id)
        if file_resp.get("error"):
            return _fmt(file_resp)

        fv = (file_resp.get("data") or {}).get("fileVersion") or {}
        file_version_id = str(fv.get("id") or "")
        file_name = fv.get("name") or ""
        if not file_version_id:
            return json.dumps({
                "error": True,
                "message": f"Could not resolve a file version from file_id={file_id}",
            })

        ext = file_name.rsplit(".", 1)[-1].lower() if "." in file_name else ""
        if ext not in ("ipt", "iam"):
            return json.dumps({
                "error": True,
                "message": (
                    f"STEP publish only supports .ipt and .iam files. "
                    f"File {file_name!r} has extension {ext!r}."
                ),
            })

        result = await api.submit_job(
            vault_id=vault_id,
            job_type=f"Autodesk.Vault.STEP.Create.{ext}",
            params={
                "FileVersionId": file_version_id,
                "UpdatePdfOption": "False",
                "UpdateViewOption": "False",
            },
            description=description or f"STEP Create: {file_name}",
            priority=priority,
        )
        return _fmt(result)

    @mcp.tool()
    async def vault_get_job(job_id: str, vault_id_param: str = "") -> str:
        """
        Get a job's metadata and current status by its ID.

        Use this to poll a job submitted via vault_submit_job. Status values:
        Ready (queued), Running, Success, Failure.

        Args:
            job_id: The job ID returned by vault_submit_job.
            vault_id_param: Vault ID. Leave empty to use the vault from config.json.
        """
        result = await api.get_job_by_id(
            vault_id=_resolved_vault(vault_id_param), job_id=job_id
        )
        return _view(result, slim.strip_noise)

    # ------------------------------------------------------------------
    # PDF watermarking
    # ------------------------------------------------------------------

    @mcp.tool()
    async def vault_watermark_pdfs_in_folder(
        folder_id: str,
        watermark_text: str,
        output_dir: str,
        vault_id_param: str = "",
        font_size: int = 80,
        color: str = "#888888",
        opacity: float = 0.3,
        rotation: float = 45.0,
        limit: int = 200,
    ) -> str:
        """
        Download every PDF in a Vault folder, apply a centered watermark to all
        pages, and save the watermarked copies to a local directory.

        Outputs are saved as ``{output_dir}/{original_name}`` — the original
        Vault files are never modified. The watermark is rendered with reportlab
        and merged with pypdf, so each page gets a same-size overlay.

        Args:
            folder_id: The Vault folder ID to scan. Use vault_get_folder_contents
                first if you don't know the ID.
            watermark_text: The text to stamp on every page (e.g. "DRAFT", a
                customer name, "PRELIMINARY — DO NOT BUILD").
            output_dir: Local directory where the watermarked PDFs will be
                written. Created if it doesn't exist.
            vault_id_param: Vault ID. Leave empty to use the vault from config.json.
            font_size: Watermark font size in points (default 80).
            color: Hex color "#RRGGBB" (default "#888888" gray).
            opacity: Fill opacity 0.0–1.0 (default 0.3, semi-transparent).
            rotation: Rotation in degrees, 0 = horizontal (default 45).
            limit: Max number of folder entries to inspect (default 200).
        """
        vault = _resolved_vault(vault_id_param)
        out_path = Path(output_dir).expanduser().resolve()
        out_path.mkdir(parents=True, exist_ok=True)

        listing = await api.get_folder_contents(
            vault_id=vault, folder_id=folder_id, limit=limit
        )
        if listing["error"]:
            return _fmt({"step": "list_folder", "result": listing})

        entries = _extract_collection(listing.get("data"))
        pdfs = [
            f for f in entries
            if str(f.get("name", "")).lower().endswith(".pdf")
        ]
        if not pdfs:
            return _fmt({
                "step": "filter_pdfs",
                "message": "No .pdf files found in the folder.",
                "folder_id": folder_id,
                "entries_seen": len(entries),
            })

        results: List[Dict[str, Any]] = []
        used_names: set = set()

        for f in pdfs:
            name = str(f.get("name") or "unknown.pdf")
            fv_id, _ = _pick_latest_version(f)
            if not fv_id:
                fv_id = _extract_id(f)
            if not fv_id:
                results.append({
                    "file": name, "ok": False,
                    "error": "could not resolve file-version ID",
                })
                continue

            dl = await api.download_file_version_content(
                vault_id=vault, file_version_id=fv_id
            )
            if dl["error"]:
                results.append({
                    "file": name, "ok": False, "file_version_id": fv_id,
                    "error": f"download failed: {dl['data']}",
                })
                continue

            try:
                watermarked = apply_watermark(
                    dl["data"],
                    watermark_text,
                    font_size=font_size,
                    color=color,
                    opacity=opacity,
                    rotation=rotation,
                )
            except Exception as exc:
                results.append({
                    "file": name, "ok": False, "file_version_id": fv_id,
                    "error": f"watermark failed: {exc}",
                })
                continue

            safe_name = _safe_filename(name, used_names)
            used_names.add(safe_name)
            target = out_path / safe_name
            target.write_bytes(watermarked)
            results.append({
                "file": name, "ok": True, "file_version_id": fv_id,
                "path": str(target), "size_bytes": len(watermarked),
            })

        succeeded = sum(1 for r in results if r["ok"])
        return _fmt({
            "folder_id": folder_id,
            "watermark_text": watermark_text,
            "output_dir": str(out_path),
            "total": len(results),
            "succeeded": succeeded,
            "failed": len(results) - succeeded,
            "files": results,
        })

    # ------------------------------------------------------------------
    # BOM → Purchasing Sheet (Simplifyber)
    # ------------------------------------------------------------------

    @mcp.tool()
    async def vault_generate_purchasing_sheet(
        part_number: str,
        output_dir: str = "",
        vault_id_param: str = "",
        limit: int = 200,
    ) -> str:
        """
        End-to-end: look up a part number in Vault, fetch its BOM, enrich the
        rows from the SharePoint 'purchased items.xlsx' reference file, and
        write a Simplifyber-branded purchasing Excel sheet to disk.

        This is the main tool for the purchasing workflow — it bundles the
        equivalent of vault_get_bom_by_part_number + sheet generation into a
        single call. The output file is named "{part_number}-PurchasingExport.xlsx".

        Args:
            part_number: Assembly / job number to look up (e.g. "MFG-00037").
            output_dir: Folder where the .xlsx is saved. Defaults to the user's Desktop.
            vault_id_param: Vault ID. Leave empty to use the vault from config.json.
            limit: Maximum number of BOM rows to include in the lookup.

        Returns:
            JSON with output_path, matched_parts, total_purchased_parts,
            unmatched_parts, and warnings. Includes a top-level error key if the
            BOM lookup itself failed.
        """
        vault = _resolved_vault(vault_id_param)
        found = await _resolve_item(vault, part_number)
        if "error" in found:
            return _fmt(found["error"])
        notes = found["notes"]
        item_version_id = found["item_version_id"]

        bom = await api.get_item_bom(
            vault_id=vault, item_version_id=item_version_id, limit=limit
        )
        if bom["error"]:
            return _fmt({"step": "get_item_bom", "result": bom})

        # Slim rows carry the quantity from the BOM links; the raw itemVersions
        # list has none and its lower-case keys never matched VAULT_FIELD_MAP.
        bom_rows = slim.item_bom(bom.get("data") or {}, item_version_id)["rows"]
        if not bom_rows:
            return _fmt({
                "step": "get_item_bom",
                "message": "BOM lookup succeeded but returned no rows.",
                "matched_item": slim.item(found["master"]),
                "item_version_id": item_version_id,
            })

        result = bom_purchasing.generate_from_vault_bom(
            vault_bom_response={"rows": bom_rows},
            assembly_number=part_number,
            output_dir=output_dir,
        )
        if notes:
            result.setdefault("warnings", []).extend(notes)
        result["matched_item"] = slim.item(found["master"])
        result["item_version_id"] = item_version_id
        return _fmt(result)

    @mcp.tool()
    def vault_generate_purchasing_sheet_from_vault_bom(
        vault_bom_json: str,
        assembly_number: str,
        output_dir: str = "",
    ) -> str:
        """
        Build a purchasing sheet from an already-fetched Vault BOM payload.
        Use this when you've already called vault_get_bom_by_part_number and
        want to feed its JSON output back in (e.g. to inspect the BOM first
        and only then generate the sheet).

        For the common case — "give me a purchasing sheet for MFG-00037" —
        prefer vault_generate_purchasing_sheet, which does the BOM lookup
        for you in a single call.

        Args:
            vault_bom_json: The JSON string returned by
                vault_get_bom_by_part_number (its 'rows' key, or the 'bom'
                key of a raw=True response).
            assembly_number: Assembly or job number label (e.g. "MFG-00037").
            output_dir: Folder for the .xlsx. Defaults to the user's Desktop.

        Returns:
            JSON with output_path, matched_parts, total_purchased_parts,
            unmatched_parts, and warnings.
        """
        try:
            payload = json.loads(vault_bom_json)
        except json.JSONDecodeError as exc:
            return json.dumps({
                "error": True,
                "message": f"Invalid JSON in vault_bom_json: {exc}",
            })

        result = bom_purchasing.generate_from_vault_bom(
            vault_bom_response=payload,
            assembly_number=assembly_number,
            output_dir=output_dir,
        )
        return _fmt(result)

    @mcp.tool()
    def vault_generate_purchasing_sheet_from_file(
        bom_file_path: str,
        assembly_number: str,
        output_dir: str = "",
    ) -> str:
        """
        Build a purchasing sheet from a manually exported BOM file. Use this
        when working from a file on disk rather than a live Vault lookup.

        Accepts a Vault BOM export or an Inventor BOM export (auto-detected):
        .xlsx, .xls, .csv, or tab-delimited .txt. For Inventor, use a
        Structured / All-Levels view and include at least Item, Part Number,
        and QTY (Description, Unit QTY, BOM Structure, REV, and Material are
        used when present).

        Args:
            bom_file_path: Absolute path to the BOM export file.
            assembly_number: Assembly or job number label (e.g. "MFG-00037").
            output_dir: Folder for the output .xlsx. Defaults to the BOM file's folder.

        Returns:
            JSON with output_path, matched_parts, total_purchased_parts,
            unmatched_parts, and warnings.
        """
        result = bom_purchasing.generate_from_file(
            bom_file_path=bom_file_path,
            assembly_number=assembly_number,
            output_dir=output_dir,
        )
        return _fmt(result)

    @mcp.tool()
    def vault_lookup_purchased_part(part_number: str) -> str:
        """
        Look up purchasing data for a single part number from the
        SharePoint/OneDrive 'purchased items.xlsx' reference file.

        Useful for diagnosing why a row in a generated sheet is missing vendor
        data, or for fetching a vendor / cost / lead-time for a specific part
        without generating a full sheet.

        Args:
            part_number: Exact part number (e.g. "CD-001234").
        """
        return _fmt(bom_purchasing.lookup_part(part_number))

    @mcp.tool()
    def vault_get_purchased_items_reference_status() -> str:
        """
        Check whether the purchased-items reference file (purchased items.xlsx)
        can be located on this machine and is readable. Returns the path,
        row count, and column list when found, or the expected path and a
        diagnostic note when not.
        """
        return _fmt(bom_purchasing.reference_file_status())

    # ------------------------------------------------------------------
    # Release workflow — pre-flight readiness report
    # ------------------------------------------------------------------

    @mcp.tool()
    async def vault_release_readiness_report(
        part_number: str,
        category_override: str = "",
        rules_path: str = "",
    ) -> str:
        """
        Run the property-compliance check for a part / assembly + every BOM
        child, and return a Markdown release-readiness report.

        This is step 1+2 of the full release workflow (see scripts/release_workflow.py).
        Use it BEFORE running any sync-properties / lifecycle-release work — it
        surfaces every non-compliant property so the user knows exactly what to
        fix before the destructive steps.

        Args:
            part_number: The top-level Vault part number (e.g. "SF-001702").
            category_override: Force a specific rule-set category instead of
                auto-detecting from the item's Category Name.
            rules_path: Override the path to the rules JSON. Defaults to
                item_property_rules.json next to mcp_server.py.
        """
        try:
            # Reload on every call so edits to scripts/check_item_properties.py
            # (alias maps, evaluate_rule, etc.) and item_property_rules.json
            # take effect without restarting the MCP server.
            import importlib
            import sys
            mod_name = "check_item_properties"
            if mod_name in sys.modules:
                importlib.reload(sys.modules[mod_name])
            from check_item_properties import (  # type: ignore
                check_part_number,
                format_markdown_report,
                DEFAULT_RULES_PATH,
            )
        except ImportError as exc:
            return _fmt({"error": True, "message": f"release workflow modules unavailable: {exc}"})

        config_path = Path(__file__).resolve().parent / "config.json"
        rp = Path(rules_path) if rules_path else DEFAULT_RULES_PATH
        try:
            result = await check_part_number(
                part_number,
                config_path=config_path,
                rules_path=rp,
                category_override=category_override,
                recursive=True,
            )
        except Exception as exc:  # noqa: BLE001 — surface any error to the caller
            return _fmt({"error": True, "message": f"{type(exc).__name__}: {exc}"})

        return format_markdown_report(result)

    # ------------------------------------------------------------------
    # Item property writes (SOAP via the PowerShell SDK bridge)
    # ------------------------------------------------------------------

    # Hard ceiling on per-call batch size. Each item costs ~1-2s of
    # PowerShell subprocess time even with parallel lookups; 50 keeps a
    # single MCP request comfortably under typical client timeouts and
    # bounds Vault audit-log churn.
    _MAX_BATCH_ITEMS = 50

    def _coerce_id_list(raw: str) -> tuple[list[int], Optional[str]]:
        """Parse the ``item_ids_json`` arg into a list of ints.

        Accepts a JSON array (`'[12345, 12346]'`), a single int as a JSON
        scalar (`'12345'`), or a comma-separated string (`'12345, 12346'`)
        for chat ergonomics. Returns ``(ids, error_message)`` — only one
        is non-empty.
        """
        s = (raw or "").strip()
        if not s:
            return [], "item_ids_json must not be empty."
        try:
            parsed: Any = json.loads(s)
        except json.JSONDecodeError:
            # Tolerate "12345, 12346" — common when humans hand-type the arg.
            parsed = [p for p in (x.strip() for x in s.split(",")) if p]
        if isinstance(parsed, (int, str)):
            parsed = [parsed]
        if not isinstance(parsed, list) or not parsed:
            return [], "item_ids_json must be a JSON array or scalar of item IDs."

        ids: list[int] = []
        for raw_id in parsed:
            try:
                ids.append(int(str(raw_id).strip()))
            except (TypeError, ValueError):
                return [], f"item_ids_json contains a non-numeric ID: {raw_id!r}"
        return ids, None

    def _state_of(props: dict[str, Any]) -> str:
        for key in ("State", "Lifecycle State", "lifecycleState"):
            v = props.get(key)
            if isinstance(v, str) and v.strip():
                return v.strip()
        return ""

    @mcp.tool()
    def vault_update_item_properties(
        item_ids_json: str,
        properties_json: str,
    ) -> str:
        """
        Apply the SAME set of properties to one or more Vault item-version
        IDs in a single SOAP call.

        This is the low-level write — call it when you already know the
        item-version IDs (from vault_search_items / vault_get_item) and want
        to write the SAME property dict to all of them. For per-item fixes
        keyed by part number (the typical "fix the readiness report"
        workflow), use ``vault_fix_item_properties`` instead — it does the
        lookups, the state-guard, and groups items by identical fix dict
        for you.

        Property names may be Vault display names ("Engr Approved",
        "Description (Item,CO)") or system names ("EngrApproved").

        Constraints (enforced by Vault, not by this tool):
          * Every item must be in 'Work in Progress'. The whole batch fails
            atomically if any item is released — there is no partial-write.
            Use ``vault_fix_item_properties`` for per-item state guarding.
          * The signed-in user must have write rights on the category.

        Args:
            item_ids_json: JSON array of item-version IDs (e.g.
                ``"[12345, 12346]"``), a single ID as a JSON scalar
                (``"12345"``), or a comma-separated string
                (``"12345, 12346"``). Capped at 50 IDs per call.
            properties_json: JSON object mapping property name to value
                (e.g. ``'{"Material": "Stainless Steel 304",
                "Engr Approved": "Yes"}'``). Applied to every ID.

        Returns:
            JSON with ``updated`` (count Vault reports), ``item_ids``,
            and ``properties``. On failure, ``{"error": True,
            "message": "..."}``.
        """
        ids, id_err = _coerce_id_list(item_ids_json)
        if id_err:
            return _fmt({"error": True, "message": id_err})
        if len(ids) > _MAX_BATCH_ITEMS:
            return _fmt({
                "error": True,
                "message": (
                    f"Batch too large: {len(ids)} IDs (max "
                    f"{_MAX_BATCH_ITEMS}). Split into smaller calls."
                ),
            })

        try:
            props = json.loads(properties_json)
        except json.JSONDecodeError as exc:
            return _fmt({
                "error": True,
                "message": f"Invalid JSON in properties_json: {exc}",
            })
        if not isinstance(props, dict) or not props:
            return _fmt({
                "error": True,
                "message": "properties_json must be a non-empty JSON object.",
            })

        try:
            from vault_sdk import update_item_properties, VaultSDKError  # type: ignore
        except ImportError as exc:
            return _fmt({
                "error": True,
                "message": f"Vault SDK bridge unavailable: {exc}",
            })

        try:
            result = update_item_properties(ids, props)
        except VaultSDKError as exc:
            return _fmt({
                "error": True,
                "message": f"UpdateItemProperties failed: {exc}",
                "item_ids": ids,
                "properties": props,
            })
        except Exception as exc:  # noqa: BLE001
            return _fmt({
                "error": True,
                "message": f"{type(exc).__name__}: {exc}",
                "item_ids": ids,
                "properties": props,
            })

        return _fmt({
            "updated": int(result.get("updated", 0)) if isinstance(result, dict) else 0,
            "item_ids": ids,
            "properties": props,
        })

    @mcp.tool()
    def vault_fix_item_properties(fixes_json: str) -> str:
        """
        Batch property fixer — accepts 1-50 part numbers, each with its own
        set of property fixes, and applies them in one MCP call.

        This is the tool for the typical workflow:
          1. ``vault_release_readiness_report("SF-001702")`` lists every
             non-compliant property across the BOM tree.
          2. Build a fixes object keyed by part number with only the
             properties that need to change.
          3. Pass it here in a single call; receive a per-item report.
          4. Re-run the readiness report to confirm.

        How the batch is executed:
          * Part-number lookups run in parallel (up to 8 concurrent
            PowerShell processes) so 50 lookups complete in seconds rather
            than minutes.
          * Items not in 'Work in Progress' are SKIPPED with a clear reason
            — they don't fail the rest of the batch. Vault rejects property
            writes on released items; revise them first then retry.
          * Items that successfully look up are GROUPED by identical fix
            dict, and each group is written in a single SOAP call. So
            "set Engineer = Zak on 30 parts" is one Vault write, not 30.
          * Per-item failures (lookup error, write error) are captured in
            the result without aborting the rest of the batch.

        Property names accept either Vault display names ("Material",
        "Engr Approved") or system names ("EngrApproved"). Empty string
        clears a property.

        Args:
            fixes_json: JSON object mapping part number to a fix dict.
                Single-item shape:
                    ``'{"SF-001702": {"Material": "Stainless Steel 304"}}'``
                Batch shape (up to 50):
                    ``'{"SF-001702": {"Material": "Stainless Steel 304"},
                       "SF-001703": {"Engr Approved": "Yes"},
                       "SF-001704": {"Engineer": "Zak", "Project": "Apollo"}}'``

        Returns:
            JSON with:
              * ``summary``: counts (total / succeeded / skipped / failed)
                and ``soap_calls`` (how many grouped writes were issued).
              * ``results``: per-part list. Each entry has
                ``part_number``, ``status`` ("ok" | "skipped" | "failed"),
                ``item_id`` / ``master_id`` / ``state`` when known, plus
                ``before`` / ``after`` / ``changed`` for "ok" items or
                ``reason`` / ``message`` for non-OK items.
        """
        try:
            fixes_raw = json.loads(fixes_json)
        except json.JSONDecodeError as exc:
            return _fmt({
                "error": True,
                "message": f"Invalid JSON in fixes_json: {exc}",
            })
        if not isinstance(fixes_raw, dict) or not fixes_raw:
            return _fmt({
                "error": True,
                "message": (
                    "fixes_json must be a non-empty JSON object of "
                    "{part_number: {property: value, ...}, ...}."
                ),
            })

        # Validate and normalise every entry up front so we don't burn
        # PowerShell roundtrips on garbage input.
        normalised: dict[str, dict[str, Any]] = {}
        for pn, props in fixes_raw.items():
            pn_clean = str(pn).strip()
            if not pn_clean:
                return _fmt({
                    "error": True,
                    "message": "fixes_json contains an empty part number key.",
                })
            if not isinstance(props, dict) or not props:
                return _fmt({
                    "error": True,
                    "message": (
                        f"fixes_json[{pn_clean!r}] must be a non-empty "
                        "object of {property: value}."
                    ),
                })
            normalised[pn_clean] = dict(props)

        if len(normalised) > _MAX_BATCH_ITEMS:
            return _fmt({
                "error": True,
                "message": (
                    f"Batch too large: {len(normalised)} part numbers "
                    f"(max {_MAX_BATCH_ITEMS}). Split into smaller calls."
                ),
            })

        try:
            from vault_sdk import (  # type: ignore
                lookup_item,
                update_item_properties,
                VaultSDKError,
            )
        except ImportError as exc:
            return _fmt({
                "error": True,
                "message": f"Vault SDK bridge unavailable: {exc}",
            })

        # ---- Phase 1: parallel lookups -----------------------------------
        # Each lookup is its own PowerShell subprocess, so threading gives
        # real concurrency. Cap at 8 to avoid swamping the box / Vault.
        from concurrent.futures import ThreadPoolExecutor

        results_by_pn: dict[str, dict[str, Any]] = {}

        def _do_lookup(pn: str) -> tuple[str, dict[str, Any]]:
            try:
                return pn, lookup_item(pn)
            except VaultSDKError as exc:
                return pn, {"_error": f"LookupItem failed: {exc}"}
            except Exception as exc:  # noqa: BLE001
                return pn, {"_error": f"{type(exc).__name__}: {exc}"}

        max_workers = min(8, len(normalised))
        with ThreadPoolExecutor(max_workers=max_workers) as pool:
            for pn, item in pool.map(_do_lookup, normalised.keys()):
                results_by_pn[pn] = {"_lookup": item}

        # ---- Phase 2: classify each item ----------------------------------
        # Build the per-part status now; group writeable items by their
        # property dict so each unique dict is one SOAP call.
        write_groups: dict[str, dict[str, Any]] = {}
        license_blocked = False
        for pn, fixes in normalised.items():
            entry = results_by_pn[pn]
            item = entry["_lookup"]

            if isinstance(item, dict) and item.get("_error"):
                err_msg = item["_error"]
                is_license = "Failed to acquire a license" in err_msg
                if is_license:
                    license_blocked = True
                results_by_pn[pn] = {
                    "part_number": pn,
                    "status": "failed",
                    "reason": "license_unavailable" if is_license else "lookup_error",
                    "message": err_msg,
                    "attempted": fixes,
                }
                continue

            if not isinstance(item, dict) or not item.get("found"):
                results_by_pn[pn] = {
                    "part_number": pn,
                    "status": "failed",
                    "reason": "not_found",
                    "message": f"No item found in Vault for part number {pn!r}.",
                    "attempted": fixes,
                }
                continue

            item_id = item.get("id")
            master_id = item.get("masterId")
            if not item_id:
                results_by_pn[pn] = {
                    "part_number": pn,
                    "status": "failed",
                    "reason": "no_item_id",
                    "message": "lookup_item returned no item-version ID.",
                    "attempted": fixes,
                }
                continue

            current_props = item.get("properties") or {}
            state_name = _state_of(current_props)
            if state_name and state_name.lower() != "work in progress":
                results_by_pn[pn] = {
                    "part_number": pn,
                    "status": "skipped",
                    "reason": "released",
                    "message": (
                        f"Item is in state '{state_name}'. Vault only allows "
                        "property writes to items in 'Work in Progress'. "
                        "Revise it first, then retry."
                    ),
                    "item_id": item_id,
                    "master_id": master_id,
                    "state": state_name,
                    "attempted": fixes,
                }
                continue

            # Cleared all guards — queue for write. Group by the JSON-
            # canonical fix dict so identical fixes share a SOAP call.
            group_key = json.dumps(fixes, sort_keys=True, default=str)
            group = write_groups.setdefault(group_key, {
                "fixes": fixes,
                "items": [],
            })
            group["items"].append({
                "part_number": pn,
                "item_id": int(item_id),
                "master_id": master_id,
                "state": state_name,
                "before": {name: current_props.get(name) for name in fixes},
            })

        # ---- Phase 3: grouped writes -------------------------------------
        soap_calls = 0
        for group in write_groups.values():
            fixes = group["fixes"]
            items = group["items"]
            ids = [it["item_id"] for it in items]
            soap_calls += 1
            try:
                write_result = update_item_properties(ids, fixes)
                updated_count = (
                    int(write_result.get("updated", 0))
                    if isinstance(write_result, dict) else 0
                )
            except VaultSDKError as exc:
                msg = f"UpdateItemProperties failed: {exc}"
                is_license = "Failed to acquire a license" in str(exc)
                if is_license:
                    license_blocked = True
                reason = "license_unavailable" if is_license else "write_error"
                for it in items:
                    results_by_pn[it["part_number"]] = {
                        "part_number": it["part_number"],
                        "status": "failed",
                        "reason": reason,
                        "message": msg,
                        "item_id": it["item_id"],
                        "master_id": it["master_id"],
                        "state": it["state"],
                        "attempted": fixes,
                    }
                continue
            except Exception as exc:  # noqa: BLE001
                msg = f"{type(exc).__name__}: {exc}"
                is_license = "Failed to acquire a license" in msg
                if is_license:
                    license_blocked = True
                reason = "license_unavailable" if is_license else "write_error"
                for it in items:
                    results_by_pn[it["part_number"]] = {
                        "part_number": it["part_number"],
                        "status": "failed",
                        "reason": reason,
                        "message": msg,
                        "item_id": it["item_id"],
                        "master_id": it["master_id"],
                        "state": it["state"],
                        "attempted": fixes,
                    }
                continue

            for it in items:
                changed = {
                    name: new_val
                    for name, new_val in fixes.items()
                    if it["before"].get(name) != new_val
                }
                results_by_pn[it["part_number"]] = {
                    "part_number": it["part_number"],
                    "status": "ok",
                    "item_id": it["item_id"],
                    "master_id": it["master_id"],
                    "state": it["state"],
                    "updated": updated_count,
                    "before": it["before"],
                    "after": fixes,
                    "changed": changed,
                }

        # ---- Phase 4: assemble summary ------------------------------------
        ordered_results = [results_by_pn[pn] for pn in normalised.keys()]
        succeeded = sum(1 for r in ordered_results if r["status"] == "ok")
        skipped = sum(1 for r in ordered_results if r["status"] == "skipped")
        failed = sum(1 for r in ordered_results if r["status"] == "failed")

        envelope: dict[str, Any] = {
            "summary": {
                "total": len(ordered_results),
                "succeeded": succeeded,
                "skipped": skipped,
                "failed": failed,
                "soap_calls": soap_calls,
            },
            "results": ordered_results,
            "note": (
                "Re-run vault_release_readiness_report on the parent "
                "assembly to confirm the items now pass compliance."
            ),
        }
        if license_blocked and succeeded == 0 and soap_calls == 0:
            envelope["error_class"] = "license_unavailable"
            envelope["remediation"] = (
                "Vault refused the SDK sign-in for every AuthenticationFlags "
                "combo. The seat is held by another client signed in as the "
                "MCP service account, OR all named-user seats are in use. "
                "Most common cause on this host: Inventor or Vault Explorer "
                "running as the same user. Close those clients (or sign out "
                "the Vault Add-In inside Inventor) and retry. If that does "
                "not clear it, check ADMS Console → License Usage and "
                "consider restarting the Autodesk Data Management Server "
                "service. No payload was modified — safe to retry as-is."
            )
        return _fmt(envelope)

    @mcp.tool()
    async def vault_release_workflow_overview() -> str:
        """
        Return a short overview of the full release workflow: the six steps,
        what each one does, and how to invoke the interactive CLI driver.
        Useful as a primer when the user asks "how do I release X?".
        """
        return (
            "**Vault release workflow** — `python scripts/release_workflow.py <part_number>`\n"
            "\n"
            "1. **Compliance check** — walk the BOM and run every item against `item_property_rules.json`.\n"
            "2. **Readiness report** — markdown report listing every non-compliant property. The CLI hard-stops here unless `--force` is passed.\n"
            "3. **Sync properties** — submit `Autodesk.Vault.SyncProperties` for every CAD file in the tree.\n"
            "4. **Get files local** — REST-download every referenced file into the local workfolder.\n"
            "5. **Inventor rebuild** — open the top `.iam`, `Update2()`, and save (drives Inventor via COM).\n"
            "6. **Release CAD** — SOAP `UpdateFileLifeCycleStates` for every file.\n"
            "7. **Release items** — SOAP `UpdateItemLifeCycleStates` for the top item + every child.\n"
            "\n"
            "Use the `vault_release_readiness_report` MCP tool for steps 1+2 from chat.\n"
            "To fix non-compliant properties without leaving chat, call "
            "`vault_fix_item_properties(fixes_json)` with up to 50 part "
            "numbers in one call: "
            '`{\"SF-001702\": {\"Material\": \"Stainless Steel 304\"}, '
            '\"SF-001703\": {\"Engr Approved\": \"Yes\"}}`. Lookups run in '
            "parallel, items not in 'Work in Progress' are skipped (not "
            "failed), and items with identical fix dicts are written in "
            "one SOAP call. The response gives a per-part status with "
            "before/after/changed. Re-run the readiness report to "
            "confirm. For a known item-version ID list with the SAME "
            "props, `vault_update_item_properties` is the lower-level "
            "single-SOAP-call write."
        )

    # McMaster -> Microsoft List price updates (additive; safe no-op if absent).
    try:
        import supplier_pricing.mcp_tools as _supplier_pricing_tools
        _supplier_pricing_tools.register(mcp)
    except Exception:  # pragma: no cover - never block server startup on this
        pass

    return mcp
