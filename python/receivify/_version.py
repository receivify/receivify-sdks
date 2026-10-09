"""Single source of truth for the package version.

Kept in its own module with nothing but a literal assignment so that
``setuptools``' ``dynamic = ["version"]`` can read it statically, without
importing the package at build time.
"""

__version__ = "1.0.0"
