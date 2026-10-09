"""
Write tools: lifecycle changes, item categories, file properties, check-out.

Vault REST v2 has no endpoints for these, so the writes go through the SOAP
SDK bridge (scripts/vault_sdk.py). Lookups and previews use REST, which is
fast and needs no license seat; only the confirmed write signs in to the SDK.

Every tool previews by default. ``confirm=True`` applies exactly the changes
the preview listed, so an agent shows the preview, gets a yes, and repeats
the call with ``confirm=True``.
"""

import asyncio
import json
from typing import Any, Awaitable, Callable, Dict, List, Optional, Tuple

import vault_slim as slim

CAD_EXTENSIONS = {"ipt", "iam", "idw", "dwg", "ipn"}
APPLY_HINT = "Nothing changed yet. Call again with confirm=true to apply the rows marked will_change."


def _dump(obj: Any) -> str:
    return json.dumps(obj, ensure_ascii=False, default=str)


def parse_targets(raw: str) -> List[str]:
    """Accept a JSON list, a JSON string, or a comma-separated list."""
    s = (raw or "").strip()
    if not s:
        return []
    try:
        parsed = json.loads(s)
    except json.JSONDecodeError:
        parsed = s.split(",")
    if isinstance(parsed, (str, int)):
        parsed = [parsed]
    out: List[str] = []
    for t in parsed:
        t = str(t).strip()
        if t and t not in out:
            out.append(t)
    return out


def _lifecycle_def_id(record: Dict[str, Any]) -> Optional[int]:
    lc = record.get("lifecycleState") or {}
    raw = (lc.get("lifecycleDefinition") or {}).get("id")
    return int(raw) if str(raw or "").isdigit() else None


def _file_property_names(fv: Dict[str, Any]) -> Dict[str, Tuple[str, bool]]:
    """Map lower-cased display and system names to (display name, is_system)
    for every property the file record carries."""
    out: Dict[str, Tuple[str, bool]] = {}
    for p in fv.get("properties") or []:
        d = p.get("definition") or {}
        disp = str(d.get("displayName") or "")
        for name in (disp, str(d.get("systemName") or "")):
            if name:
                out[name.lower()] = (disp, bool(d.get("isSystem")))
    return out


def _normalise_props(
    props: Dict[str, Any], known: Dict[str, Tuple[str, bool]]
) -> Tuple[Dict[str, Any], List[str]]:
    """Return props keyed by display name, plus a message per bad name."""
    good: Dict[str, Any] = {}
    bad: List[str] = []
    for name, value in props.items():
        hit = known.get(str(name).strip().lower())
        if not hit:
            close = sorted({disp for key, (disp, sys_) in known.items()
                            if not sys_ and str(name).strip().lower() in key})
            hint = f" (did you mean {', '.join(close)}?)" if close else ""
            bad.append(f"no file property {name!r}{hint}")
        elif hit[1]:
            bad.append(f"{hit[0]!r} is a system property and cannot be written")
        else:
            good[hit[0]] = value
    return good, bad


def _sdk():
    """Import the SDK bridge lazily so the server starts without it."""
    import vault_sdk  # scripts/ is on sys.path via mcp_server
    return vault_sdk


def _sdk_error(exc: Exception) -> Dict[str, Any]:
    msg = f"{type(exc).__name__}: {exc}"
    out: Dict[str, Any] = {"error": True, "message": msg}
    if "Failed to acquire a license" in msg:
        out["remediation"] = (
            "Vault refused the SDK sign-in license. Usually Vault Explorer or "
            "Inventor is open under the same Vault account; close them (or sign "
            "out of the Vault add-in) and retry. Nothing was changed."
        )
    return out


def register(
    mcp,
    api,
    resolved_vault: Callable[[Optional[str]], str],
    resolve_item: Callable[[str, str], Awaitable[Dict[str, Any]]],
) -> None:
    """Attach the write tools to ``mcp``. ``resolve_item(vault, part_number)``
    is the server's exact-match item resolver."""

    async def resolve_file(vault: str, ref: str) -> Tuple[Optional[Dict[str, Any]], str]:
        """Find a file by master/version id or exact name; return its latest
        file-version record (with properties) or an error message."""
        ref = ref.strip()
        file_id = ref
        if not ref.isdigit():
            search = await api.search_files(vault_id=vault, query=ref, latest_only=True, limit=20)
            if search.get("error"):
                return None, f"search failed: {search.get('data')}"
            files = slim._records(search.get("data"))
            low = ref.lower()
            exact = [f for f in files if str(f.get("name", "")).lower() == low]
            if not exact:
                exact = [f for f in files if str(f.get("name", "")).lower().rsplit(".", 1)[0] == low]
            if not exact:
                return None, f"no file named {ref!r}"
            if len(exact) > 1:
                names = ", ".join(sorted(str(f.get("name")) for f in exact))
                return None, f"{ref!r} matches several files ({names}); give the full file name"
            file_id = str((exact[0].get("file") or {}).get("id") or exact[0].get("id"))
        resp = await api.get_file_by_id(vault_id=vault, file_id=file_id)
        if resp.get("error"):
            return None, f"file lookup failed: {resp.get('data')}"
        fv = (resp.get("data") or {}).get("fileVersion")
        if not fv:
            return None, f"no file version for {ref!r}"
        return fv, ""

    async def resolve_items(vault: str, part_numbers: List[str]) -> List[Dict[str, Any]]:
        """Exact part-number matches only; anything else becomes an error row."""
        rows = []
        for pn in part_numbers:
            found = await resolve_item(vault, pn)
            if "error" in found:
                rows.append({"target": pn, "status": "error", "reason": "not found"})
                continue
            iv = found["master"].get("itemVersion") or found.get("item_version") or {}
            if str(iv.get("number", "")).lower() != pn.lower():
                rows.append({"target": pn, "status": "error", "reason": "no exact part-number match"})
                continue
            rows.append({"target": pn, "_master_id": int(found["master"]["id"]), "_iv": iv})
        return rows

    def public(rows: List[Dict[str, Any]]) -> List[Dict[str, Any]]:
        return [{k: v for k, v in r.items() if not k.startswith("_")} for r in rows]

    state_cache: Dict[Tuple[str, int], List[Dict[str, Any]]] = {}

    async def target_state_id(vault: str, def_id: Optional[int], target: str) -> Optional[int]:
        """Find ``target`` among the states of lifecycle ``def_id`` (REST, no seat)."""
        if def_id is None:
            return None
        key = (vault, def_id)
        if key not in state_cache:
            resp = await api.get_lifecycle_definition(vault, str(def_id))
            if resp.get("error"):
                return None
            state_cache[key] = (resp.get("data") or {}).get("states") or []
        needle = target.strip().lower()
        for s in state_cache[key]:
            if needle in (str(s.get("name", "")).lower(), str(s.get("displayName", "")).lower()):
                return int(s["id"])
        return None

    async def apply_grouped(rows, key: str, write) -> int:
        """Run ``write(master_ids, group_value)`` once per distinct group value.

        Marks rows ``changed`` or ``error``; returns the number of SDK calls.
        """
        groups: Dict[Any, List[Dict[str, Any]]] = {}
        for r in rows:
            if r["status"] == "will_change":
                groups.setdefault(r[key], []).append(r)
        for value, members in groups.items():
            ids = [m["_master_id"] for m in members]
            try:
                await asyncio.to_thread(write, ids, value)
                for m in members:
                    m["status"] = "changed"
            except Exception as exc:  # noqa: BLE001 -- surface per-row
                err = _sdk_error(exc)
                for m in members:
                    m["status"] = "error"
                    m["reason"] = err["message"]
                    if "remediation" in err:
                        m["remediation"] = err["remediation"]
        return len(groups)

    def summary(rows, confirm: bool, calls: int = 0) -> str:
        counts: Dict[str, int] = {}
        for r in rows:
            counts[r["status"]] = counts.get(r["status"], 0) + 1
        out: Dict[str, Any] = {"applied": confirm, "counts": counts, "rows": public(rows)}
        if confirm:
            out["sdk_calls"] = calls
        elif counts.get("will_change"):
            out["next"] = APPLY_HINT
        return _dump(out)

    # ------------------------------------------------------------------
    # Lifecycle state
    # ------------------------------------------------------------------

    @mcp.tool()
    async def vault_change_item_state(
        part_numbers: str,
        target_state: str,
        comment: str = "",
        confirm: bool = False,
        vault_id_param: str = "",
    ) -> str:
        """
        Move engineering items to another lifecycle state, e.g. "Work in
        Progress" -> "In Review" -> "Released", or back to "Work in Progress"
        to revise.

        Previews by default: each row shows current -> target state and
        will_change / no_change / error. Show the preview to the user, then
        call again with confirm=true. Vault enforces its own transition rules
        and permissions (e.g. releasing an assembly whose children are not
        released fails); those errors come back per row.

        Args:
            part_numbers: One part number, a comma list, or a JSON list
                ("SF-001942" or '["SF-001942", "SF-001940"]'). Exact matches only.
            target_state: State name, e.g. "Released". Matched inside each
                item's own lifecycle definition.
            comment: Lifecycle comment recorded in Vault history.
            confirm: false = preview only; true = apply.
            vault_id_param: Vault ID. Leave empty to use the vault from config.json.
        """
        vault = resolved_vault(vault_id_param)
        targets = parse_targets(part_numbers)
        if not targets:
            return _dump({"error": True, "message": "part_numbers is empty."})
        rows = await resolve_items(vault, targets)
        for r in rows:
            if r.get("status") == "error":
                continue
            iv = r["_iv"]
            r["current_state"] = slim._state(iv)
            r["target_state"] = target_state
            state_id = await target_state_id(vault, _lifecycle_def_id(iv), target_state)
            if state_id is None:
                r.update(status="error", reason=f"state {target_state!r} not in this item's lifecycle")
            elif r["current_state"].lower() == target_state.strip().lower():
                r["status"] = "no_change"
            else:
                r.update(status="will_change", _state_id=state_id)
        if not confirm:
            return summary(rows, False)
        sdk = _sdk()
        calls = await apply_grouped(
            rows, "_state_id",
            lambda ids, sid: sdk.update_item_lifecycle_states(ids, sid, comment=comment),
        )
        return summary(rows, True, calls)

    @mcp.tool()
    async def vault_change_file_state(
        files: str,
        target_state: str,
        comment: str = "",
        confirm: bool = False,
        vault_id_param: str = "",
    ) -> str:
        """
        Move CAD files (or any Vault files) to another lifecycle state, e.g.
        release CD-001621.iam and its drawing.

        Previews by default; call again with confirm=true to apply. Files that
        are checked out are skipped. Vault enforces transition rules (e.g. a
        parent cannot be released before its children) and reports per row.

        Args:
            files: File names or ids: one, a comma list, or a JSON list.
                Use full names when a part has several files (CD-001621.iam
                vs CD-001621.idw).
            target_state: State name, e.g. "Released" or "Work in Progress".
            comment: Lifecycle comment recorded in Vault history.
            confirm: false = preview only; true = apply.
            vault_id_param: Vault ID. Leave empty to use the vault from config.json.
        """
        vault = resolved_vault(vault_id_param)
        targets = parse_targets(files)
        if not targets:
            return _dump({"error": True, "message": "files is empty."})
        rows = []
        seen: set = set()
        for ref in targets:
            fv, err = await resolve_file(vault, ref)
            if err:
                rows.append({"target": ref, "status": "error", "reason": err})
                continue
            if fv.get("id") in seen:
                continue
            seen.add(fv.get("id"))
            row = {
                "target": fv.get("name"),
                "current_state": slim._state(fv),
                "target_state": target_state,
                "_master_id": int((fv.get("file") or {}).get("id")),
            }
            state_id = await target_state_id(vault, _lifecycle_def_id(fv), target_state)
            if fv.get("isCheckedOut"):
                row.update(status="skipped", reason=f"checked out by {fv.get('checkoutUserName') or 'someone'}")
            elif state_id is None:
                row.update(status="error", reason=f"state {target_state!r} not in this file's lifecycle")
            elif row["current_state"].lower() == target_state.strip().lower():
                row["status"] = "no_change"
            else:
                row.update(status="will_change", _state_id=state_id)
            rows.append(row)
        if not confirm:
            return summary(rows, False)
        sdk = _sdk()
        calls = await apply_grouped(
            rows, "_state_id",
            lambda ids, sid: sdk.update_file_lifecycle_states(ids, sid, comment=comment),
        )
        return summary(rows, True, calls)

    # ------------------------------------------------------------------
    # Item category
    # ------------------------------------------------------------------

    @mcp.tool()
    async def vault_change_item_category(
        part_numbers: str,
        category: str,
        comment: str = "",
        confirm: bool = False,
        vault_id_param: str = "",
    ) -> str:
        """
        Change the category of engineering items, e.g. "Part - Engineering"
        -> "Part - Purchased". Items must be in Work in Progress.

        Previews by default; call again with confirm=true to apply.

        Args:
            part_numbers: One part number, a comma list, or a JSON list.
            category: Target category name (exact, case-insensitive).
            comment: Comment recorded in Vault history.
            confirm: false = preview only; true = apply.
            vault_id_param: Vault ID. Leave empty to use the vault from config.json.
        """
        vault = resolved_vault(vault_id_param)
        targets = parse_targets(part_numbers)
        if not targets:
            return _dump({"error": True, "message": "part_numbers is empty."})
        try:
            cats = (await asyncio.to_thread(_sdk().get_item_categories)).get("categories") or []
        except Exception as exc:  # noqa: BLE001
            return _dump(_sdk_error(exc))
        cat = next((c for c in cats if str(c.get("name", "")).lower() == category.strip().lower()), None)
        if not cat:
            return _dump({
                "error": True,
                "message": f"Unknown category {category!r}.",
                "categories": sorted(str(c.get("name")) for c in cats),
            })
        rows = await resolve_items(vault, targets)
        for r in rows:
            if r.get("status") == "error":
                continue
            iv = r["_iv"]
            r["current_category"] = iv.get("category")
            r["target_category"] = cat["name"]
            state = slim._state(iv)
            if str(iv.get("category", "")).lower() == str(cat["name"]).lower():
                r["status"] = "no_change"
            elif state.lower() != "work in progress":
                r.update(status="skipped", reason=f"item is {state!r}; move it to Work in Progress first")
            else:
                r.update(status="will_change", _cat_id=int(cat["id"]))
        if not confirm:
            return summary(rows, False)
        sdk = _sdk()
        calls = await apply_grouped(
            rows, "_cat_id",
            lambda ids, cid: sdk.update_item_categories(ids, cid, comment=comment),
        )
        return summary(rows, True, calls)

    # ------------------------------------------------------------------
    # Check-out
    # ------------------------------------------------------------------

    async def _checkout_tool(file: str, confirm: bool, vault_id_param: str, comment: str, undo: bool) -> str:
        vault = resolved_vault(vault_id_param)
        fv, err = await resolve_file(vault, file)
        if err:
            return _dump({"error": True, "message": err})
        holder = fv.get("checkoutUserName") or ("someone" if fv.get("isCheckedOut") else "")
        row: Dict[str, Any] = {
            "target": fv.get("name"),
            "checked_out_by": holder or None,
            "_master_id": int((fv.get("file") or {}).get("id")),
        }
        if undo and not fv.get("isCheckedOut"):
            row["status"] = "no_change"
        elif not undo and fv.get("isCheckedOut"):
            row.update(status="skipped", reason=f"already checked out by {holder}")
        else:
            row["status"] = "will_change"
            if undo:
                row["warning"] = (
                    f"{holder}'s pending edits to this file can no longer be checked in "
                    "once the checkout is undone."
                )
        if not confirm or row["status"] != "will_change":
            return summary([row], False) if not confirm else summary([row], True)
        sdk = _sdk()
        try:
            if undo:
                await asyncio.to_thread(sdk.undo_checkout_file, row["_master_id"])
            else:
                await asyncio.to_thread(sdk.checkout_file, row["_master_id"], comment=comment)
            row["status"] = "changed"
        except Exception as exc:  # noqa: BLE001
            err_d = _sdk_error(exc)
            row.update(status="error", reason=err_d["message"])
            if "remediation" in err_d:
                row["remediation"] = err_d["remediation"]
        return summary([row], True, 1)

    @mcp.tool()
    async def vault_check_out_file(
        file: str, comment: str = "", confirm: bool = False, vault_id_param: str = ""
    ) -> str:
        """
        Check out a file in Vault to reserve it, without downloading it.
        Previews by default; call again with confirm=true.

        Args:
            file: File name (e.g. "CD-001621.iam") or file id.
            comment: Checkout comment.
            confirm: false = preview only; true = apply.
            vault_id_param: Vault ID. Leave empty to use the vault from config.json.
        """
        return await _checkout_tool(file, confirm, vault_id_param, comment, undo=False)

    @mcp.tool()
    async def vault_undo_check_out_file(
        file: str, confirm: bool = False, vault_id_param: str = ""
    ) -> str:
        """
        Undo a file's checkout, e.g. to free a file someone left checked out.
        Their unsaved check-in for that file is lost. Undoing another user's
        checkout needs Vault administrator rights. Previews by default; call
        again with confirm=true.

        Args:
            file: File name (e.g. "SF_Vault.ipj") or file id.
            confirm: false = preview only; true = apply.
            vault_id_param: Vault ID. Leave empty to use the vault from config.json.
        """
        return await _checkout_tool(file, confirm, vault_id_param, "", undo=True)

    # ------------------------------------------------------------------
    # File properties
    # ------------------------------------------------------------------

    @mcp.tool()
    async def vault_update_file_properties(
        fixes_json: str,
        sync_to_cad: bool = True,
        confirm: bool = False,
        vault_id_param: str = "",
    ) -> str:
        """
        Set Vault properties on files, e.g. Description or Material on
        CD-001621.ipt. The item-side equivalent is vault_fix_item_properties.

        Writing creates a new property-only version of each file. With
        sync_to_cad=true, a SyncProperties job is queued per CAD file so the
        Job Processor writes mapped values into the .ipt/.iam/.idw itself.
        Files that are checked out or in a released state are skipped.
        Previews by default (current -> new value); call again with confirm=true.

        Args:
            fixes_json: JSON object of file -> {property: value}, e.g.
                '{"CD-001621.ipt": {"Description": "bladder", "Material": "PA12"}}'.
                Property names are Vault display or system names.
            sync_to_cad: Queue a SyncProperties job for each changed CAD file.
            confirm: false = preview only; true = apply.
            vault_id_param: Vault ID. Leave empty to use the vault from config.json.
        """
        vault = resolved_vault(vault_id_param)
        try:
            fixes = json.loads(fixes_json)
        except json.JSONDecodeError as exc:
            return _dump({"error": True, "message": f"Invalid JSON in fixes_json: {exc}"})
        if not isinstance(fixes, dict) or not fixes or not all(isinstance(v, dict) and v for v in fixes.values()):
            return _dump({"error": True, "message": 'fixes_json must look like {"<file>": {"<property>": "<value>"}}.'})

        rows = []
        for ref, props in fixes.items():
            fv, err = await resolve_file(vault, str(ref))
            if err:
                rows.append({"target": ref, "status": "error", "reason": err})
                continue
            current = slim.properties(fv)
            known = _file_property_names(fv)
            props, bad = _normalise_props(props, known)
            lc = fv.get("lifecycleState") or {}
            row: Dict[str, Any] = {
                "target": fv.get("name"),
                "changes": {k: {"from": current.get(k), "to": v} for k, v in props.items()},
                "_master_id": int((fv.get("file") or {}).get("id")),
                "_props": props,
                "_cad": str(fv.get("name", "")).lower().rsplit(".", 1)[-1] in CAD_EXTENSIONS,
            }
            if bad:
                row.update(status="error", reason="; ".join(bad))
            elif fv.get("isCheckedOut"):
                row.update(status="skipped", reason=f"checked out by {fv.get('checkoutUserName') or 'someone'}")
            elif lc.get("isReleasedState"):
                row.update(status="skipped", reason=f"file is {lc.get('name')!r}; move it to Work in Progress first")
            elif all(str(current.get(k, "")) == str(v) for k, v in props.items()):
                row["status"] = "no_change"
            else:
                row["status"] = "will_change"
            rows.append(row)
        if not confirm:
            return summary(rows, False)

        sdk = _sdk()
        calls = 0
        groups: Dict[str, List[Dict[str, Any]]] = {}
        for r in rows:
            if r["status"] == "will_change":
                groups.setdefault(json.dumps(r["_props"], sort_keys=True), []).append(r)
        for members in groups.values():
            calls += 1
            try:
                result = await asyncio.to_thread(
                    sdk.update_file_properties, [m["_master_id"] for m in members], members[0]["_props"]
                )
            except Exception as exc:  # noqa: BLE001
                err_d = _sdk_error(exc)
                for m in members:
                    m.update(status="error", reason=err_d["message"])
                continue
            new_ids = {int(f["masterId"]): int(f["id"]) for f in (result or {}).get("files") or []}
            for m in members:
                m["status"] = "changed"
                new_id = new_ids.get(m["_master_id"])
                if sync_to_cad and m["_cad"] and new_id:
                    job = await api.submit_job(
                        vault_id=vault,
                        job_type="Autodesk.Vault.SyncProperties",
                        params={"FileVersionId": str(new_id)},
                        description=f"SyncProperties: {m['target']}",
                        priority=10,
                    )
                    m["sync_job"] = (
                        f"failed: {job.get('data')}" if job.get("error")
                        else (job.get("data") or {}).get("id")
                    )
        return summary(rows, True, calls)
