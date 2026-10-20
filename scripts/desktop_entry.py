"""Absolute-import entry point for PyInstaller (a package __main__ is not a standalone script)."""
import sys
from moz_checker.__main__ import main

if __name__ == '__main__':
    sys.exit(main())
