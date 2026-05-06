"""Pytest configuration for dapr-agent tests."""

import sys
from pathlib import Path

# Add the dapr-agent source root to sys.path so tests can import
# agent.py, mcp_bridge.py, a2a_server.py without package installation.
sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
