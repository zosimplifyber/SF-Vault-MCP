"""Tiny evaluator for the formulas the purchasing writer emits.

openpyxl cannot calculate. This covers cell refs, + - * /, comparisons, IF,
ISNUMBER and ROUNDUP; any other function raises NameError so a new one is noticed.
"""
import math
import re

_REF = r"\$?[A-Z]{1,3}\$?\d+"


def evaluate(ws, ref_or_formula):
    """Value of a cell ("T5") or of a formula string ("=A1*B1")."""
    text = ref_or_formula
    if not text.startswith("="):
        v = ws[text.replace("$", "")].value
        if not (isinstance(v, str) and v.startswith("=")):
            return 0 if v is None else v
        text = v
    expr = text[1:]

    def is_number(m):
        v = ws[m.group(1).replace("$", "")].value
        if isinstance(v, str) and v.startswith("="):
            return "True"
        return str(isinstance(v, (int, float)) and not isinstance(v, bool))

    expr = re.sub(rf"ISNUMBER\(({_REF})\)", is_number, expr)
    expr = re.sub(_REF, lambda m: f"({evaluate(ws, m.group(0))!r})", expr)
    return eval(expr, {"IF": lambda c, a, b: a if c else b,
                       "ROUNDUP": lambda x, d: math.ceil(round(x, 9))})  # noqa: S307 - test-only, our own formulas
