"""
Vault REST API Client
Wraps the Autodesk Vault Data API v2 endpoints.
Base URL: {servername}/AutodeskDM/Services/api/vault/v2
"""

import asyncio
import httpx
import logging
from typing import Any, Dict, List, Optional

logger = logging.getLogger(__name__)

API_PATH = "/AutodeskDM/Services/api/vault/v2"


class VaultRestAPI:
    """
    Client for the Autodesk Vault Data REST API v2.
    All methods return a dict: {"error": bool, "status_code": int, "data": Any}
    """

    def __init__(self, servername: str):
        self.base_url = servername.rstrip("/") + API_PATH
        self._session_token: Optional[str] = None
        self._vault_id: Optional[str] = None
        # Credentials cached on successful sign-in so _request() can silently
        # re-authenticate when Vault times out the session token (default ~30
        # min idle). Without this, every call after timeout returns 401 with
        # the misleading "do not have permissions to download this file"
        # message and the only fix is restarting the MCP subprocess.
        self._creds: Optional[Dict[str, str]] = None
        self._reauth_lock: Optional[asyncio.Lock] = None

    # ------------------------------------------------------------------
    # Session helpers
    # ------------------------------------------------------------------

    def set_session(self, token: str, vault_id: str) -> None:
        """Store the active session token and vault ID after sign-in."""
        self._session_token = token
        self._vault_id = vault_id
        logger.info("Session stored (vault_id=%s)", vault_id)

    def clear_session(self) -> None:
        self._session_token = None
        self._vault_id = None
        self._creds = None
        logger.info("Session cleared")

    def _ensure_reauth_lock(self) -> asyncio.Lock:
        if self._reauth_lock is None:
            self._reauth_lock = asyncio.Lock()
        return self._reauth_lock

    async def _try_reauth(self, expired_token: Optional[str]) -> bool:
        """Re-sign in using cached credentials. Returns True on success.

        ``expired_token`` is the token that was on the request that just got a
        401 — when several concurrent calls all 401 at once we only want to
        re-auth once, so a caller whose token has already been replaced (by
        another caller's re-auth) skips and retries with the new token.
        """
        if not self._creds:
            return False
        async with self._ensure_reauth_lock():
            if self._session_token and self._session_token != expired_token:
                return True
            creds = self._creds
            logger.info(
                "Session likely expired — re-authenticating as %s (database: %s)",
                creds.get("username"), creds.get("database"),
            )
            result = await self.create_session(
                database=creds["database"],
                username=creds["username"],
                password=creds["password"],
                app_code=creds.get("app_code", ""),
            )
            return not result["error"]

    @property
    def is_authenticated(self) -> bool:
        return self._session_token is not None

    def _auth_headers(self) -> Dict[str, str]:
        headers: Dict[str, str] = {"Content-Type": "application/json"}
        if self._session_token:
            token = self._session_token
            if not token.startswith("Bearer "):
                token = f"Bearer {token}"
            headers["Authorization"] = token
        return headers

    # ------------------------------------------------------------------
    # HTTP transport
    # ------------------------------------------------------------------

    async def _send_once(
        self,
        method: str,
        endpoint: str,
        *,
        params: Optional[Dict[str, Any]],
        json_data: Optional[Dict[str, Any]],
        extra_headers: Optional[Dict[str, str]],
        timeout: float,
        include_auth: bool = True,
    ) -> Dict[str, Any]:
        headers = (
            self._auth_headers() if include_auth
            else {"Content-Type": "application/json"}
        )
        if extra_headers:
            headers.update(extra_headers)

        url = self.base_url + endpoint
        log_headers = {
            k: ("Bearer ***" if k == "Authorization" else v)
            for k, v in headers.items()
        }
        logger.info("%s %s  headers=%s  params=%s", method, url, log_headers, params)

        try:
            async with httpx.AsyncClient(verify=False) as client:
                resp = await client.request(
                    method,
                    url,
                    headers=headers,
                    params=params,
                    json=json_data,
                    timeout=timeout,
                    follow_redirects=True,
                )
        except httpx.TimeoutException:
            logger.error("Request timed out: %s %s", method, url)
            return {"error": True, "status_code": 504,
                    "data": {"message": "Request timed out"},
                    "_token_used": self._session_token}
        except httpx.RequestError as exc:
            logger.error("Connection error: %s", exc)
            return {"error": True, "status_code": 503,
                    "data": {"message": str(exc)},
                    "_token_used": self._session_token}

        logger.info("Response %s", resp.status_code)
        try:
            data = resp.json()
        except Exception:
            data = {"content": resp.text}

        if resp.status_code >= 400:
            return {"error": True, "status_code": resp.status_code, "data": data,
                    "_token_used": headers.get("Authorization")}
        return {"error": False, "status_code": resp.status_code, "data": data,
                "_token_used": headers.get("Authorization")}

    async def _request(
        self,
        method: str,
        endpoint: str,
        *,
        params: Optional[Dict[str, Any]] = None,
        json_data: Optional[Dict[str, Any]] = None,
        extra_headers: Optional[Dict[str, str]] = None,
        timeout: float = 30.0,
        include_auth: bool = True,
    ) -> Dict[str, Any]:
        result = await self._send_once(
            method, endpoint,
            params=params, json_data=json_data,
            extra_headers=extra_headers, timeout=timeout,
            include_auth=include_auth,
        )

        # Auto-reauth on 401: Vault returns 401 + errorCode 8000 ("do not
        # have permissions to download this file") for both real ACL denials
        # and expired sessions. Re-auth once with cached creds and retry —
        # if it was an ACL issue we'll get the same 401 back.
        if (
            result["status_code"] == 401
            and not endpoint.startswith("/sessions")
            and self._creds is not None
        ):
            expired = result.pop("_token_used", None)
            # Strip the "Bearer " prefix to compare against self._session_token
            expired_token = (
                expired[len("Bearer "):] if isinstance(expired, str) and expired.startswith("Bearer ")
                else self._session_token
            )
            reauthed = await self._try_reauth(expired_token)
            if reauthed:
                logger.info("Re-authenticated; retrying %s %s", method, endpoint)
                result = await self._send_once(
                    method, endpoint,
                    params=params, json_data=json_data,
                    extra_headers=extra_headers, timeout=timeout,
                    include_auth=include_auth,
                )

        result.pop("_token_used", None)
        if result["error"] and result["status_code"] not in (503, 504):
            logger.error("API error %s: %s", result["status_code"], result["data"])
        return result

    # ------------------------------------------------------------------
    # Informational
    # ------------------------------------------------------------------

    async def get_server_info(self) -> Dict[str, Any]:
        """GET /server-info — product version and metadata."""
        return await self._request("GET", "/server-info")

    async def get_api_spec(self) -> Dict[str, Any]:
        """GET /openapi-spec.yml — OpenAPI specification."""
        return await self._request("GET", "/openapi-spec.yml")

    # ------------------------------------------------------------------
    # Authentication
    # ------------------------------------------------------------------

    async def create_session(
        self,
        database: str,
        username: str,
        password: str,
        app_code: str = "",
    ) -> Dict[str, Any]:
        """
        POST /sessions — sign in and obtain a Bearer token.
        On success the method automatically calls set_session().
        """
        result = await self._request(
            "POST",
            "/sessions",
            json_data={
                "input": {
                    "vault": database,
                    "userName": username,
                    "password": password,
                    "appCode": app_code,
                }
            },
            # Sign-in is unauthenticated. Never attach a (possibly expired)
            # Bearer token: the Vault server 401s the sign-in itself when a
            # stale Authorization header rides along — which is exactly the
            # auto-reauth-on-401 case, where the cached token has just expired.
            include_auth=False,
        )
        if not result["error"]:
            data = result["data"]
            # accessToken starts with "V:" and is used as the Bearer token
            access_token = data.get("accessToken", "")
            # vault ID is nested inside vaultInformation
            vault_id = str(
                (data.get("vaultInformation") or {}).get("id", "")
                or data.get("vaultId", "")
                or ""
            )
            if access_token:
                self.set_session(access_token, vault_id)
                # Cache credentials so _request() can auto-reauth on session
                # expiration. Stored only on success — no point keeping
                # rejected creds.
                self._creds = {
                    "database": database,
                    "username": username,
                    "password": password,
                    "app_code": app_code,
                }
        return result

    async def delete_session(self, session_id: str) -> Dict[str, Any]:
        """DELETE /sessions/{sessionId} — sign out."""
        result = await self._request("DELETE", f"/sessions/{session_id}")
        self.clear_session()
        return result

    # ------------------------------------------------------------------
    # Accounts
    # ------------------------------------------------------------------

    async def get_groups(
        self,
        limit: int = 100,
        cursor_state: Optional[str] = None,
    ) -> Dict[str, Any]:
        """GET /groups — list all groups."""
        params: Dict[str, Any] = {"limit": limit}
        if cursor_state:
            params["cursorState"] = cursor_state
        return await self._request("GET", "/groups", params=params)

    async def get_group_by_id(self, group_id: str) -> Dict[str, Any]:
        """GET /groups/{id} — get a specific group."""
        return await self._request("GET", f"/groups/{group_id}")

    async def get_users(
        self,
        limit: int = 100,
        cursor_state: Optional[str] = None,
    ) -> Dict[str, Any]:
        """GET /users — list all users."""
        params: Dict[str, Any] = {"limit": limit}
        if cursor_state:
            params["cursorState"] = cursor_state
        return await self._request("GET", "/users", params=params)

    async def get_user_by_id(self, user_id: str) -> Dict[str, Any]:
        """GET /users/{id} — get a specific user."""
        return await self._request("GET", f"/users/{user_id}")

    async def get_profile_attribute_definitions(
        self,
        filter_association: Optional[str] = None,
        limit: int = 100,
        cursor_state: Optional[str] = None,
    ) -> Dict[str, Any]:
        """GET /profile-attribute-definitions."""
        params: Dict[str, Any] = {"limit": limit}
        if filter_association:
            params["filter[association]"] = filter_association
        if cursor_state:
            params["cursorState"] = cursor_state
        return await self._request("GET", "/profile-attribute-definitions", params=params)

    # ------------------------------------------------------------------
    # Vaults
    # ------------------------------------------------------------------

    async def get_vaults(self) -> Dict[str, Any]:
        """GET /vaults — list all vaults accessible by the current session."""
        return await self._request("GET", "/vaults")

    async def get_vault_by_id(self, vault_id: str) -> Dict[str, Any]:
        """GET /vaults/{id} — get a specific vault."""
        return await self._request("GET", f"/vaults/{vault_id}")

    # ------------------------------------------------------------------
    # Folders
    # ------------------------------------------------------------------

    async def get_folder_contents(
        self,
        vault_id: str,
        folder_id: str = "$",
        *,
        query: Optional[str] = None,
        search_content: bool = False,
        search_sub_folders: bool = False,
        extended_models: bool = False,
        prop_def_ids: Optional[str] = None,
        limit: int = 100,
        cursor_state: Optional[str] = None,
    ) -> Dict[str, Any]:
        """GET /vaults/{vaultId}/folders/{id}/contents."""
        resolved = vault_id or self._vault_id or ""
        fid = "root" if folder_id in ("root", "$", "") else folder_id
        params: Dict[str, Any] = {
            "limit": limit,
            "option.searchContent": str(search_content).lower(),
            "option.searchSubFolders": str(search_sub_folders).lower(),
            "extendedModels": str(extended_models).lower(),
        }
        if query:
            params["q"] = query
        if prop_def_ids:
            params["propDefIds"] = prop_def_ids
        if cursor_state:
            params["cursorState"] = cursor_state
        return await self._request(
            "GET",
            f"/vaults/{resolved}/folders/{fid}/contents",
            params=params,
        )

    async def get_folder_by_id(self, vault_id: str, folder_id: str) -> Dict[str, Any]:
        """GET /vaults/{vaultId}/folders/{id}."""
        resolved = vault_id or self._vault_id or ""
        fid = "root" if folder_id in ("root", "$", "") else folder_id
        return await self._request("GET", f"/vaults/{resolved}/folders/{fid}")

    # ------------------------------------------------------------------
    # Files
    # ------------------------------------------------------------------

    async def get_file_by_id(
        self,
        vault_id: str,
        file_id: str,
        *,
        released_only: bool = False,
    ) -> Dict[str, Any]:
        """GET /vaults/{vaultId}/files/{id}."""
        resolved = vault_id or self._vault_id or ""
        params = {"option.releasedOnly": str(released_only).lower()}
        return await self._request(
            "GET", f"/vaults/{resolved}/files/{file_id}", params=params
        )

    async def get_file_versions(
        self,
        vault_id: str,
        file_id: str,
        *,
        limit: int = 100,
    ) -> Dict[str, Any]:
        """GET /vaults/{vaultId}/files/{id}/versions — version history of a master file."""
        resolved = vault_id or self._vault_id or ""
        return await self._request(
            "GET", f"/vaults/{resolved}/files/{file_id}/versions", params={"limit": limit}
        )

    async def get_file_version_signed_url(
        self, vault_id: str, file_version_id: str
    ) -> Dict[str, Any]:
        """GET /vaults/{vaultId}/file-versions/{id}/signedurl — time-limited download link.

        Vault answers with a server-relative path; it is made absolute here so
        the caller can fetch it without knowing the server name.
        """
        resolved = vault_id or self._vault_id or ""
        result = await self._request(
            "GET", f"/vaults/{resolved}/file-versions/{file_version_id}/signedurl"
        )
        data = result.get("data")
        if not result.get("error") and isinstance(data, dict) and data.get("url"):
            host = self.base_url[: -len(API_PATH)] if self.base_url.endswith(API_PATH) else self.base_url
            data["url"] = host + data["url"]
        return result

    async def get_file_parents(
        self, vault_id: str, file_version_id: str, *, limit: int = 100
    ) -> Dict[str, Any]:
        """GET /vaults/{vaultId}/file-versions/{id}/parents — CAD where-used."""
        resolved = vault_id or self._vault_id or ""
        return await self._request(
            "GET",
            f"/vaults/{resolved}/file-versions/{file_version_id}/parents",
            params={"limit": limit},
        )

    async def get_file_item_versions(
        self, vault_id: str, file_version_id: str
    ) -> Dict[str, Any]:
        """GET /vaults/{vaultId}/file-versions/{id}/item-versions — items linked to a file."""
        resolved = vault_id or self._vault_id or ""
        return await self._request(
            "GET", f"/vaults/{resolved}/file-versions/{file_version_id}/item-versions"
        )

    async def get_file_uses(
        self,
        vault_id: str,
        file_version_id: str,
        *,
        prop_def_ids: Optional[str] = None,
        limit: int = 200,
        cursor_state: Optional[str] = None,
    ) -> Dict[str, Any]:
        """GET /vaults/{vaultId}/file-versions/{id}/uses — child file associations (CAD BOM).

        Rows come back as ``{parentFile, childFile, fileAssocType}``. Passing
        ``prop_def_ids`` (``"all"`` or a comma-separated list) enriches both the
        parent and every child with their properties in this single call — no
        per-child hydration needed. As with ``search_file_versions``, the
        selector must be spelled ``option[propDefIds]``.
        """
        resolved = vault_id or self._vault_id or ""
        params: Dict[str, Any] = {"limit": limit}
        if prop_def_ids:
            params["option[propDefIds]"] = prop_def_ids
        if cursor_state:
            params["cursorState"] = cursor_state
        return await self._request(
            "GET",
            f"/vaults/{resolved}/file-versions/{file_version_id}/uses",
            params=params,
        )

    async def download_file_version_content(
        self, vault_id: str, file_version_id: str, *, timeout: float = 180.0
    ) -> Dict[str, Any]:
        """GET /vaults/{vaultId}/file-versions/{id}/content — raw file bytes.

        On success the standard envelope's ``data`` field holds raw ``bytes``
        rather than a JSON-decoded dict. Callers must not feed the result to
        ``json.dumps`` without first extracting and discarding the bytes.
        """
        resolved = vault_id or self._vault_id or ""
        url = self.base_url + f"/vaults/{resolved}/file-versions/{file_version_id}/content"

        result, token_used = await self._download_once(url, timeout)
        if result["status_code"] == 401 and self._creds is not None:
            if await self._try_reauth(token_used):
                logger.info("Re-authenticated; retrying download %s", url)
                result, _ = await self._download_once(url, timeout)
        return result

    async def _download_once(
        self, url: str, timeout: float
    ) -> tuple[Dict[str, Any], Optional[str]]:
        headers = self._auth_headers()
        headers.pop("Content-Type", None)
        token_used = self._session_token

        log_headers = {
            k: ("Bearer ***" if k == "Authorization" else v)
            for k, v in headers.items()
        }
        logger.info("GET %s  headers=%s  (binary)", url, log_headers)

        try:
            async with httpx.AsyncClient(verify=False) as client:
                resp = await client.get(
                    url, headers=headers, timeout=timeout, follow_redirects=True
                )
            logger.info("Response %s  bytes=%d", resp.status_code, len(resp.content))
            if resp.status_code >= 400:
                try:
                    err = resp.json()
                except Exception:
                    err = {"content": resp.text[:500]}
                return ({"error": True, "status_code": resp.status_code, "data": err},
                        token_used)
            return ({"error": False, "status_code": resp.status_code, "data": resp.content},
                    token_used)
        except httpx.TimeoutException:
            logger.error("Download timed out: %s", url)
            return ({"error": True, "status_code": 504,
                     "data": {"message": "Download timed out"}}, token_used)
        except httpx.RequestError as exc:
            logger.error("Download error: %s", exc)
            return ({"error": True, "status_code": 503,
                     "data": {"message": str(exc)}}, token_used)

    # ------------------------------------------------------------------
    # Search
    # ------------------------------------------------------------------

    async def search_files(
        self,
        vault_id: str,
        query: str,
        *,
        search_content: bool = False,
        search_sub_folders: bool = True,
        released_files_only: bool = False,
        released_items_only: bool = False,
        latest_only: bool = True,
        extended_models: bool = False,
        prop_def_ids: Optional[str] = None,
        sort: Optional[str] = None,
        limit: int = 100,
        cursor_state: Optional[str] = None,
    ) -> Dict[str, Any]:
        """GET /vaults/{vaultId}/search-results — basic keyword search."""
        resolved = vault_id or self._vault_id or ""
        params: Dict[str, Any] = {
            "q": query,
            "limit": limit,
            "option.searchContent": str(search_content).lower(),
            "option.searchSubFolders": str(search_sub_folders).lower(),
            "option.releasedFilesOnly": str(released_files_only).lower(),
            "option.releasedItemsOnly": str(released_items_only).lower(),
            "option.latestOnly": str(latest_only).lower(),
            "extendedModels": str(extended_models).lower(),
        }
        if prop_def_ids:
            params["propDefIds"] = prop_def_ids
        if sort:
            params["sort"] = sort
        if cursor_state:
            params["cursorState"] = cursor_state
        return await self._request(
            "GET", f"/vaults/{resolved}/search-results", params=params
        )

    async def search_file_versions(
        self,
        vault_id: str,
        query: str = "",
        *,
        prop_def_ids: str = "all",
        latest_only: bool = True,
        released_files_only: bool = False,
        category_name: Optional[str] = None,
        sort: Optional[str] = None,
        limit: int = 100,
        cursor_state: Optional[str] = None,
    ) -> Dict[str, Any]:
        """GET /vaults/{vaultId}/file-versions — file search that returns properties.

        Unlike ``search_files`` / ``get_folder_contents``, this passes the
        property selector as ``option[propDefIds]``, which is the spelling the
        file endpoints actually honour (items use a bare ``propDefIds`` — see
        ``search_items``). With the wrong spelling Vault returns 200 and simply
        omits the properties, so file UDPs silently never arrive.

        ``prop_def_ids`` accepts a comma-separated list of IDs or the literal
        ``"all"`` (the default). Responses carry properties as
        ``[{propertyDefinitionId, value}]`` plus an ``included.propertyDefinition``
        map that names them, so no separate ``/property-definitions`` call is
        needed to resolve display names.
        """
        resolved = vault_id or self._vault_id or ""
        params: Dict[str, Any] = {
            "limit": limit,
            "option[latestOnly]": str(latest_only).lower(),
            "option[releasedFilesOnly]": str(released_files_only).lower(),
        }
        if query:
            params["q"] = query
        if prop_def_ids:
            params["option[propDefIds]"] = prop_def_ids
        if category_name:
            params["filter[CategoryName]"] = category_name
        if sort:
            params["sort"] = sort
        if cursor_state:
            params["cursorState"] = cursor_state
        return await self._request(
            "GET", f"/vaults/{resolved}/file-versions", params=params
        )

    async def advanced_search(
        self,
        vault_id: str,
        body: Dict[str, Any],
        *,
        limit: int = 100,
        search_sub_folders: bool = True,
        released_files_only: bool = False,
        released_items_only: bool = False,
        latest_only: bool = True,
        cursor_state: Optional[str] = None,
    ) -> Dict[str, Any]:
        """POST /vaults/{vaultId}:advanced-search — structured criteria search.

        ``body`` is the request schema as-is: ``searchCriterias`` (required),
        plus optional ``entityTypesToSearch``, ``foldersToSearch`` and
        ``sortCriterias``. Paging and the release/latest switches are query
        parameters, not body fields.
        """
        resolved = vault_id or self._vault_id or ""
        params: Dict[str, Any] = {
            "limit": limit,
            "option[searchSubFolders]": str(search_sub_folders).lower(),
            "option[releasedFilesOnly]": str(released_files_only).lower(),
            "option[releasedItemsOnly]": str(released_items_only).lower(),
            "option[latestOnly]": str(latest_only).lower(),
        }
        if cursor_state:
            params["cursorState"] = cursor_state
        return await self._request(
            "POST", f"/vaults/{resolved}:advanced-search", params=params, json_data=body
        )

    # ------------------------------------------------------------------
    # Property definitions
    # ------------------------------------------------------------------

    async def get_property_definitions(
        self,
        vault_id: str,
        limit: int = 200,
        cursor_state: Optional[str] = None,
    ) -> Dict[str, Any]:
        """GET /vaults/{vaultId}/property-definitions."""
        resolved = vault_id or self._vault_id or ""
        params: Dict[str, Any] = {"limit": limit}
        if cursor_state:
            params["cursorState"] = cursor_state
        return await self._request(
            "GET", f"/vaults/{resolved}/property-definitions", params=params
        )

    async def get_property_definition_by_id(
        self, vault_id: str, prop_def_id: str
    ) -> Dict[str, Any]:
        """GET /vaults/{vaultId}/property-definitions/{id}."""
        resolved = vault_id or self._vault_id or ""
        return await self._request(
            "GET", f"/vaults/{resolved}/property-definitions/{prop_def_id}"
        )

    # ------------------------------------------------------------------
    # Items (BOM / Engineering items)
    # ------------------------------------------------------------------

    async def get_item_by_id(
        self, vault_id: str, item_id: str, *, prop_def_ids: Optional[str] = None,
    ) -> Dict[str, Any]:
        """GET /vaults/{vaultId}/items/{id}.

        Pass ``prop_def_ids`` (comma-separated property definition IDs) to
        include user-defined properties in the response — without it the
        endpoint returns only system fields (Number / Title / Description /
        Revision / State / Category) and the ``properties`` list is empty.
        """
        resolved = vault_id or self._vault_id or ""
        params: Dict[str, Any] = {}
        if prop_def_ids:
            params["propDefIds"] = prop_def_ids
        return await self._request(
            "GET", f"/vaults/{resolved}/items/{item_id}", params=params or None,
        )

    async def search_items(
        self,
        vault_id: str,
        query: str,
        limit: int = 100,
        cursor_state: Optional[str] = None,
        *,
        prop_def_ids: Optional[str] = None,
    ) -> Dict[str, Any]:
        """GET /vaults/{vaultId}/items — search for engineering items.

        Pass ``prop_def_ids`` to include UDPs on each result (otherwise only
        system fields come back).
        """
        resolved = vault_id or self._vault_id or ""
        params: Dict[str, Any] = {"q": query, "limit": limit}
        if cursor_state:
            params["cursorState"] = cursor_state
        if prop_def_ids:
            params["propDefIds"] = prop_def_ids
        return await self._request("GET", f"/vaults/{resolved}/items", params=params)

    async def get_item_version_history(
        self,
        vault_id: str,
        item_id: str,
        *,
        limit: int = 50,
        cursor_state: Optional[str] = None,
        prop_def_ids: Optional[str] = None,
    ) -> Dict[str, Any]:
        """GET /vaults/{vaultId}/items/{id}/versions — version history for a master item.

        Pass ``prop_def_ids`` to include UDPs on each version.
        """
        resolved = vault_id or self._vault_id or ""
        params: Dict[str, Any] = {"limit": limit}
        if cursor_state:
            params["cursorState"] = cursor_state
        if prop_def_ids:
            params["propDefIds"] = prop_def_ids
        return await self._request(
            "GET", f"/vaults/{resolved}/items/{item_id}/versions", params=params
        )

    async def get_item_change_orders(
        self,
        vault_id: str,
        item_id: str,
        *,
        limit: int = 100,
        cursor_state: Optional[str] = None,
    ) -> Dict[str, Any]:
        """GET /vaults/{vaultId}/items/{id}/change-orders."""
        resolved = vault_id or self._vault_id or ""
        params: Dict[str, Any] = {"limit": limit}
        if cursor_state:
            params["cursorState"] = cursor_state
        return await self._request(
            "GET", f"/vaults/{resolved}/items/{item_id}/change-orders", params=params
        )

    async def list_change_orders(
        self,
        vault_id: str,
        *,
        open_only: bool = False,
        limit: int = 100,
    ) -> Dict[str, Any]:
        """GET /vaults/{vaultId}/change-orders."""
        resolved = vault_id or self._vault_id or ""
        params: Dict[str, Any] = {"limit": limit}
        if open_only:
            params["filter[openCOsOnly]"] = "true"
        return await self._request(
            "GET", f"/vaults/{resolved}/change-orders", params=params
        )

    async def get_change_order(self, vault_id: str, change_order_id: str) -> Dict[str, Any]:
        """GET /vaults/{vaultId}/change-orders/{id}."""
        resolved = vault_id or self._vault_id or ""
        return await self._request(
            "GET", f"/vaults/{resolved}/change-orders/{change_order_id}"
        )

    async def get_change_order_entities(
        self, vault_id: str, change_order_id: str
    ) -> Dict[str, Any]:
        """GET /vaults/{vaultId}/change-orders/{id}/associated-entities — items and files on an ECO."""
        resolved = vault_id or self._vault_id or ""
        return await self._request(
            "GET", f"/vaults/{resolved}/change-orders/{change_order_id}/associated-entities"
        )

    # ------------------------------------------------------------------
    # Item versions & BOM
    # ------------------------------------------------------------------

    async def list_item_versions(
        self,
        vault_id: str,
        *,
        query: Optional[str] = None,
        limit: int = 100,
        cursor_state: Optional[str] = None,
        prop_def_ids: Optional[str] = None,
    ) -> Dict[str, Any]:
        """GET /vaults/{vaultId}/item-versions — list item versions, optional keyword filter.

        Pass ``prop_def_ids`` to include UDPs on each version.
        """
        resolved = vault_id or self._vault_id or ""
        params: Dict[str, Any] = {"limit": limit}
        if query:
            params["q"] = query
        if cursor_state:
            params["cursorState"] = cursor_state
        if prop_def_ids:
            params["propDefIds"] = prop_def_ids
        return await self._request(
            "GET", f"/vaults/{resolved}/item-versions", params=params
        )

    async def get_item_version_by_id(
        self, vault_id: str, item_version_id: str, *,
        prop_def_ids: Optional[str] = None,
    ) -> Dict[str, Any]:
        """GET /vaults/{vaultId}/item-versions/{id}.

        Pass ``prop_def_ids`` (comma-separated property definition IDs) to
        populate the response's ``properties`` list with the matching UDPs.
        Without it Vault returns only system fields (number, title, state,
        revision, category, etc.) and the ``properties`` array is empty.
        """
        resolved = vault_id or self._vault_id or ""
        params: Dict[str, Any] = {}
        if prop_def_ids:
            params["propDefIds"] = prop_def_ids
        return await self._request(
            "GET",
            f"/vaults/{resolved}/item-versions/{item_version_id}",
            params=params or None,
        )

    async def get_item_bom(
        self,
        vault_id: str,
        item_version_id: str,
        *,
        limit: int = 200,
        cursor_state: Optional[str] = None,
        prop_def_ids: Optional[str] = None,
    ) -> Dict[str, Any]:
        """GET /vaults/{vaultId}/item-versions/{id}/bill-of-materials — child items (BOM)."""
        resolved = vault_id or self._vault_id or ""
        params: Dict[str, Any] = {"limit": limit}
        if cursor_state:
            params["cursorState"] = cursor_state
        if prop_def_ids:
            params["propDefIds"] = prop_def_ids
        return await self._request(
            "GET",
            f"/vaults/{resolved}/item-versions/{item_version_id}/bill-of-materials",
            params=params,
        )

    async def get_item_parents(
        self,
        vault_id: str,
        item_version_id: str,
        *,
        limit: int = 100,
        cursor_state: Optional[str] = None,
    ) -> Dict[str, Any]:
        """GET /vaults/{vaultId}/item-versions/{id}/parents — where-used."""
        resolved = vault_id or self._vault_id or ""
        params: Dict[str, Any] = {"limit": limit}
        if cursor_state:
            params["cursorState"] = cursor_state
        return await self._request(
            "GET",
            f"/vaults/{resolved}/item-versions/{item_version_id}/parents",
            params=params,
        )

    async def get_item_associated_files(
        self,
        vault_id: str,
        item_version_id: str,
        *,
        limit: int = 100,
        cursor_state: Optional[str] = None,
    ) -> Dict[str, Any]:
        """GET /vaults/{vaultId}/item-versions/{id}/associated-files."""
        resolved = vault_id or self._vault_id or ""
        params: Dict[str, Any] = {"limit": limit}
        if cursor_state:
            params["cursorState"] = cursor_state
        return await self._request(
            "GET",
            f"/vaults/{resolved}/item-versions/{item_version_id}/associated-files",
            params=params,
        )

    async def get_item_thumbnail(
        self, vault_id: str, item_version_id: str
    ) -> Dict[str, Any]:
        """GET /vaults/{vaultId}/item-versions/{id}/thumbnail."""
        resolved = vault_id or self._vault_id or ""
        return await self._request(
            "GET",
            f"/vaults/{resolved}/item-versions/{item_version_id}/thumbnail",
        )

    # ------------------------------------------------------------------
    # Change orders / Lifecycle
    # ------------------------------------------------------------------

    async def get_lifecycle_definitions(
        self, vault_id: str, limit: int = 100
    ) -> Dict[str, Any]:
        """GET /vaults/{vaultId}/lifecycle-definitions."""
        resolved = vault_id or self._vault_id or ""
        return await self._request(
            "GET", f"/vaults/{resolved}/lifecycle-definitions", params={"limit": limit}
        )

    # ------------------------------------------------------------------
    # Jobs
    # ------------------------------------------------------------------

    async def submit_job(
        self,
        vault_id: str,
        job_type: str,
        *,
        params: Optional[Dict[str, str]] = None,
        description: Optional[str] = None,
        priority: Optional[int] = None,
    ) -> Dict[str, Any]:
        """POST /vaults/{vaultId}/jobs — add a job to the Vault job queue.

        Every field on the Job schema must be present, and description must be
        non-empty. Missing fields or an empty description trigger Vault error 155
        ("Illegal null parameter") even though the OpenAPI spec marks none of
        them as required.
        """
        resolved = vault_id or self._vault_id or ""
        body: Dict[str, Any] = {
            "id": "",
            "jobType": job_type,
            "priority": priority if priority is not None else 1,
            "description": description or f"Submitted via Vault MCP: {job_type}",
            "url": "",
            "params": {str(k): str(v) for k, v in (params or {}).items()},
            "isOnSite": "",
        }
        return await self._request(
            "POST", f"/vaults/{resolved}/jobs", json_data=body
        )

    async def get_job_by_id(self, vault_id: str, job_id: str) -> Dict[str, Any]:
        """GET /vaults/{vaultId}/jobs/{id} — fetch job status and metadata."""
        resolved = vault_id or self._vault_id or ""
        return await self._request("GET", f"/vaults/{resolved}/jobs/{job_id}")

    async def get_job_queue_enabled(self, vault_id: str) -> Dict[str, Any]:
        """GET /vaults/{vaultId}/jobs/job-queue-enabled — boolean queue status."""
        resolved = vault_id or self._vault_id or ""
        return await self._request(
            "GET", f"/vaults/{resolved}/jobs/job-queue-enabled"
        )
