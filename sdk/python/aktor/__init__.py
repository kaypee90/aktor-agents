"""Aktor Agents for Python: the platform API, the end-user API, and remote plugins.

    from aktor import Aktor, AktorApp, Plugin

No dependencies (the standard library only). See README.md and docs/solutions.md.
"""

from .client import Aktor, AktorError, bundle_directory
from .app import AktorApp
from .plugin import Plugin, ToolError

__all__ = ["Aktor", "AktorApp", "AktorError", "Plugin", "ToolError", "bundle_directory"]
__version__ = "0.1.0"
