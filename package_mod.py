import shutil
import sys
import zipfile
from pathlib import Path


MOD_ID = "Replay"
ROOT = Path(__file__).resolve().parent


def find_output() -> Path | None:
    candidates = [
        ROOT / "MobilePlugin" / "bin" / "Release" / "net10.0",
        ROOT,
    ]
    for candidate in candidates:
        if (candidate.joinpath(f"{MOD_ID}.dll").is_file()):
            return candidate
    return None


def main() -> int:
    version = (ROOT / "VERSION.txt").read_text(encoding="utf-8").strip()
    if not version:
        print("VERSION.txt is empty.", file=sys.stderr)
        return 1

    output = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else find_output()
    if output is None or not output.joinpath(f"{MOD_ID}.dll").is_file():
        print("Replay build output was not found.", file=sys.stderr)
        print("Build MobilePlugin/Replay.csproj first, or pass its output directory.", file=sys.stderr)
        return 1

    required_files = [f"{MOD_ID}.dll", "System.Formats.Nrbf.dll"]
    missing = [name for name in required_files if not output.joinpath(name).is_file()]
    if missing:
        print(f"Missing build files: {', '.join(missing)}", file=sys.stderr)
        return 1

    temporary = ROOT / "tmp_package"
    mod_directory = temporary / MOD_ID
    zip_path = ROOT / f"{MOD_ID}-{version}.zip"

    if temporary.exists():
        shutil.rmtree(temporary)
    if zip_path.exists():
        zip_path.unlink()
    mod_directory.mkdir(parents=True)

    for name in required_files:
        shutil.copy2(output / name, mod_directory / name)

    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as archive:
        for path in sorted(mod_directory.rglob("*")):
            if path.is_file():
                archive.write(path, path.relative_to(temporary))

    shutil.rmtree(temporary)
    print(zip_path)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
