import asyncio

from mcp_server import create_mcp_server


class _Api:
    """create_mcp_server only stores the api object at build time."""


def test_intake_tools_registered():
    mcp = create_mcp_server(api=_Api(), vault_id="1")
    names = {t.name for t in asyncio.run(mcp.list_tools())}
    assert {
        "vault_mcmaster_intake_preview",
        "vault_mcmaster_intake_commit",
        "vault_get_part_specs",
    } <= names
