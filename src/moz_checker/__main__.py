import argparse
import sys


def main():
    parser = argparse.ArgumentParser(description='Expired Domain Checker desktop')
    parser.add_argument('--smoke', action='store_true', help='Render UI offscreen and exit after readiness checks')
    args = parser.parse_args()
    if args.smoke:
        import os
        import tempfile
        from pathlib import Path
        os.environ.setdefault('QT_QPA_PLATFORM', 'offscreen')
        from PySide6.QtWidgets import QApplication
        from .store import Store
        from .models import normalize
        assert normalize("https://www.bücher.de/path") == "xn--bcher-kva.de"
        from .ui import Window
        from .presentation import apply_theme
        app = QApplication([])
        apply_theme(app)
        with tempfile.TemporaryDirectory() as directory:
            window = Window(Store(Path(directory) / 'smoke.sqlite3'))
            window.show()
            app.processEvents()
            assert window.isVisible()
            assert window.table.columnCount() == 12
            image = window.grab()
            assert not image.isNull()
            window.close()
        print('Desktop smoke passed: window rendered, SQLite initialized, 12 result columns, clean shutdown.')
        return 0
    from .ui import run
    return run()


if __name__ == '__main__':
    sys.exit(main())
