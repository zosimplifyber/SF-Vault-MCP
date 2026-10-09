"""Compact views of Vault REST payloads (vault_slim) and their purchasing hand-off."""
import vault_slim as slim


def _iv(id_, number, **extra):
    return {
        "id": id_, "number": number, "title": f"CD-{id_}", "revision": "1",
        "state": "Released", "category": "Part - Engineering", "version": 3,
        "lastModifiedDate": "2026-07-14T19:56:46.293Z", "lastModifiedUserName": "AYeh",
        "stateColor": -1, "url": "/x", "item": {"id": f"m{id_}", "url": "/m"},
        "lifecycleState": {"name": "Released", "comments": ["noise"]},
        "isLatestObsolete": False,
        "properties": [
            {"propertyDefinitionId": "1", "value": "Steel", "definition": {"displayName": "Material"}},
            {"propertyDefinitionId": "2", "value": "", "definition": {"displayName": "Empty"}},
        ],
        **extra,
    }


def _link(parent, child, qty, pos=None):
    return {"parentItemId": parent, "childItemId": child, "quantityNumber": float(qty),
            "quantity": str(qty), "units": "Each", "positionNumber": pos}


def test_item_version_keeps_reader_fields_and_drops_noise():
    out = slim.item_version(_iv("10", "SF-1"))
    assert out == {
        "number": "SF-1", "title": "CD-10", "revision": "1", "state": "Released",
        "category": "Part - Engineering", "version": 3, "modified": "2026-07-14",
        "modified_by": "AYeh", "item_version_id": "10", "item_id": "m10",
    }


def test_properties_collapse_to_name_value_and_skip_blanks():
    out = slim.item_version(_iv("10", "SF-1"), with_properties=True)
    assert out["properties"] == {"Material": "Steel"}


def test_item_bom_is_depth_first_with_dotted_rows_and_link_quantities():
    data = {
        "itemVersions": [_iv("1", "TOP"), _iv("2", "SUB"), _iv("3", "SCREW"), _iv("4", "PIN")],
        "itemBomLinks": [
            _link("1", "3", 6, "2"),
            _link("1", "2", 1, "1"),
            _link("2", "4", 4),
        ],
    }
    out = slim.item_bom(data, "1")
    assert out["assembly"]["number"] == "TOP"
    assert [(r["row"], r["number"], r["qty"]) for r in out["rows"]] == [
        ("1", "SUB", 1), ("1.1", "PIN", 4), ("2", "SCREW", 6),
    ]


def test_item_bom_survives_a_cycle():
    data = {
        "itemVersions": [_iv("1", "A"), _iv("2", "B")],
        "itemBomLinks": [_link("1", "2", 1), _link("2", "1", 1)],
    }
    rows = slim.item_bom(data, "1")["rows"]
    assert [r["number"] for r in rows] == ["B", "A"]


def test_item_parents_splits_direct_users_from_higher_assemblies():
    data = {
        "itemVersions": [_iv("9", "SCREW"), _iv("5", "SUB"), _iv("7", "TOP")],
        "itemBomLinks": [_link("5", "9", 20), _link("7", "5", 1)],
    }
    out = slim.item_parents(data, "9")
    assert [(p["number"], p["qty"]) for p in out["used_in"]] == [("SUB", 20)]
    assert [p["number"] for p in out["higher_level_assemblies"]] == ["TOP"]


def test_collection_reports_more_and_resolves_folder_paths():
    data = {
        "pagination": {"totalResults": 40, "nextUrl": "/next"},
        "results": [{
            "entityType": "FileVersion", "id": "77", "name": "CD-1.iam",
            "isCheckedOut": True, "checkoutUserName": "AYeh",
            "parentFolderId": "5", "file": {"id": "70"},
            "lifecycleState": {"name": "Work in Progress"},
        }],
        "included": {"folder": {"5": {"fullName": "$/DESIGNS"}}},
    }
    out = slim.collection(data)
    assert out["total"] == 40 and out["returned"] == 1 and "more" in out
    f = out["results"][0]
    assert f["folder"] == "$/DESIGNS"
    assert f["checked_out_by"] == "AYeh"
    assert (f["file_version_id"], f["file_id"]) == ("77", "70")
    assert f["state"] == "Work in Progress"


def test_unknown_entities_fall_back_to_stripping_urls_and_colours():
    out = slim.entity({"entityType": "User", "name": "x", "url": "/u", "stateColor": 1})
    assert out == {"entityType": "User", "name": "x"}


def test_slim_bom_rows_feed_the_purchasing_sheet():
    import bom_purchasing

    child = _iv("2", "SF-2", description="pin")
    child["properties"].append(
        {"propertyDefinitionId": "9", "value": "Buy", "definition": {"displayName": "Source"}})
    data = {
        "itemVersions": [_iv("1", "TOP"), child],
        "itemBomLinks": [_link("1", "2", 4)],
    }
    payload = slim.item_bom(data, "1")
    assert payload["rows"][0]["source"] == "Buy" and payload["rows"][0]["material"] == "Steel"
    df = bom_purchasing.vault_bom_to_dataframe(bom_purchasing.extract_bom_list(payload))
    row = df.iloc[0]
    assert (row["Number"], row["Row Order"], row["Item Qty"], row["Description (Item,CO)"], row["Source"]) == (
        "SF-2", "1", 4, "pin", "Buy",
    )
