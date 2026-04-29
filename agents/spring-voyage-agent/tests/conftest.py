"""Pytest configuration for spring-voyage-agent tests."""

import sys
from pathlib import Path

# Add the SDK source root to sys.path so tests can import the package
# without installing it.
sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
