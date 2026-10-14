"""Builds the one-page 'Install guide.pdf' that ships in the SF Vault Bridge release zip.

Usage: py -3.14 scripts/build_install_guide.py <out.pdf> <version>
"""
from __future__ import annotations

import sys
from pathlib import Path

STEPS = [
    ("Unzip", "Unzip the whole package to a folder (right-click, Extract All). Do not run files from inside the zip."),
    ("Close Inventor", "Install.cmd stops if Inventor is running and tells you which process to close."),
    ("Install", "Double-click Install.cmd. It copies the add-in for your Windows user only (no admin rights) and keeps your recent partner names."),
    ("Start Inventor", "The SF Vault Bridge tab appears on the Part, Assembly and Drawing ribbons."),
    ("Allow the add-in once", "The package is not signed yet. If the tab is missing: Tools tab, Add-In Manager, select SF Vault Bridge, tick Loaded and Load on Startup, OK. Inventor remembers this."),
    ("Sign in to Vault", "Vault tab, Log In. The Status dot on the SF Vault Bridge tab turns green when ready."),
    ("Claude Desktop (optional)", "Double-click SF Vault Bridge.mcpb and choose Install. Claude can then use Vault through your Inventor session while Inventor is open."),
]

UPDATES = ("Updates: the Status button shows 'Update available: <version>' when a newer package is in Vault "
           "($/Tools/SF Vault Bridge). Click it to download, then close Inventor, unzip and run Install.cmd again. "
           "Nothing installs by itself. To remove the add-in, run 'Uninstall SF Vault Bridge.cmd' from "
           "%APPDATA%\\Autodesk\\ApplicationPlugins.")


def build(out: Path, version: str) -> None:
    from reportlab.lib.pagesizes import letter
    from reportlab.lib.styles import ParagraphStyle, getSampleStyleSheet
    from reportlab.lib.units import inch
    from reportlab.platypus import ListFlowable, ListItem, Paragraph, SimpleDocTemplate, Spacer

    styles = getSampleStyleSheet()
    body = ParagraphStyle("body", parent=styles["BodyText"], fontSize=10.5, leading=14)
    doc = SimpleDocTemplate(str(out), pagesize=letter, leftMargin=0.9 * inch, rightMargin=0.9 * inch,
                            topMargin=0.8 * inch, bottomMargin=0.7 * inch, title=f"SF Vault Bridge {version} install guide",
                            author="Simplifyber")
    items = [ListItem(Paragraph(f"<b>{head}.</b> {text}", body), leftIndent=18) for head, text in STEPS]
    story = [
        Paragraph(f"SF Vault Bridge {version}: install guide", styles["Title"]),
        Paragraph("You need Autodesk Inventor 2026 and your Vault login. Nothing else.", body),
        Spacer(1, 10),
        ListFlowable(items, bulletType="1", bulletFontSize=10.5),
        Spacer(1, 12),
        Paragraph(UPDATES, body),
    ]
    doc.build(story)


def main(argv: list[str] | None = None) -> int:
    args = sys.argv[1:] if argv is None else argv
    if len(args) != 2:
        print(__doc__)
        return 2
    build(Path(args[0]), args[1])
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
