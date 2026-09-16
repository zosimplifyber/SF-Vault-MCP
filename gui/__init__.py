"""GUI front-ends for the Vault MCP suite.

This package exposes the Tk-based desktop GUIs that ship with the project.
Each module defines a ``launch_*`` (or ``run_*``) entry point that ``app.py``
and ``gui.launcher`` call after signing in to Vault, so the GUIs share the
single authenticated session created at launch.

* ``gui.launcher``              — Vault Integration launcher dashboard
* ``gui.release_workflow``      — Release Workflow wizard (also owns FileSearchDialog)
* ``gui.purchasing``            — Purchasing-sheet generator
* ``gui.purchasing_list_sync``  — BOM → Purchased Parts SharePoint sync
* ``gui.publish_bom``           — BOM → published PDF / STEP deliverables
* ``gui.file_property_check``   — File property compliance check
* ``gui.formed_fiber_handoff``  — Formed Fiber design-to-process handoff
* ``gui.mfg_package``           — Manufacturing package builder (item-based; off the dashboard)
* ``gui.search_dialog``         — Item search dialog used by gui.mfg_package only

The GUIs depend on root-level engine modules (``vault_rest_api``,
``mcp_server``, ``bom_purchasing``, ``mfg_package``, ``pdf_watermark``,
``formed_fiber_handoff``, ``formed_fiber_vault``, ``formed_fiber_pdf``) and on
helpers under ``scripts/`` (``check_file_properties``, ``check_item_properties``,
``inventor_automation``, ``release_workflow``). ``app.py`` adds the project root
and ``scripts/`` to ``sys.path`` before importing this package.
"""
