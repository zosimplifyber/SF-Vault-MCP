"""CAD BOM rows from Inventor's stored BOM, and their hand-off to purchasing."""
import vault_cad_tools as cad
import vault_slim as slim


def _comp(id_, name, struct="Normal", **props):
    return {"id": id_, "name": name, "type": "Part", "bomStructure": struct, "uom": "",
            "props": {k.replace("_", " "): v for k, v in props.items()}}


def _inst(parent, child, qty, structure="Default"):
    return {"parent": parent, "child": child, "qty": qty, "structure": structure}


DATA = {
    "found": True,
    "root": {"name": "TOP.iam", "checkedOut": True, "checkedIn": "2026-10-09T10:29:48-04:00"},
    "comps": [
        _comp(0, "TOP", Part_Number="TOP"),
        _comp(1, "PLATE.ipt", Source="Make"),
        _comp(2, "SUB.iam"),
        _comp(3, "SCREW.ipt", Content_Center_File="True"),
        _comp(4, "PLATEN.ipt"),
        _comp(5, "GHOST.iam", struct="Phantom"),
        _comp(6, "PIN.ipt", Source="Buy", Vendor="McMaster"),
    ],
    "insts": [
        _inst(0, 1, 1),
        _inst(0, 2, 2),
        _inst(2, 3, 8),
        _inst(0, 4, 2, structure="Reference"),
        _inst(0, 4, 2, structure="Reference"),
        _inst(0, 5, 3),
        _inst(5, 6, 4),
    ],
}


def test_rows_are_depth_first_skip_reference_and_flatten_phantoms():
    view = slim.cad_bom(DATA)
    got = [(r["row"], r["file"], r["qty"]) for r in view["rows"]]
    assert got == [
        ("1", "PLATE.ipt", 1),
        ("2", "SUB.iam", 2),
        ("2.1", "SCREW.ipt", 8),
        ("3", "PIN.ipt", 12),   # phantom GHOST x3, PIN x4 each
    ]
    assert view["assembly"]["checked_out"] is True


def test_source_wins_then_content_center_then_make():
    rows = {r["file"]: r for r in slim.cad_bom(DATA)["rows"]}
    assert cad.effective_source(rows["PIN.ipt"]) == ("Buy", False)
    assert cad.effective_source(rows["SCREW.ipt"]) == ("Buy", True)
    assert cad.effective_source(rows["SUB.iam"]) == ("Make", True)
    assert cad.missing_source(list(rows.values())) == ["SUB.iam"]


def test_inventor_table_feeds_the_purchasing_coercion():
    import bom_purchasing

    table = cad.rows_to_inventor_table(slim.cad_bom(DATA)["rows"])
    df, err = bom_purchasing.coerce_bom_dataframe(table)
    assert err is None
    assert list(df["Row Order"]) == ["1", "2", "2.1", "3"]
    assert list(df["Source"]) == ["Make", "Make", "Buy", "Buy"]
    assert list(df["Item Qty"]) == [1, 2, 8, 12]
    assert list(table["File Name"]) == ["PLATE.ipt", "SUB.iam", "SCREW.ipt", "PIN.ipt"]
