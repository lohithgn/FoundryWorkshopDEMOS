"""Generate a deterministic PDF and locally synthesized, benign audio fixture."""

from pathlib import Path
import shutil
import subprocess
import wave

ROOT = Path(__file__).resolve().parent


def pdf_bytes(text: str) -> bytes:
    lines = text.splitlines()
    commands = ["BT /F1 18 Tf 72 740 Td"]
    for index, line in enumerate(lines):
        escaped = line.replace("\\", "\\\\").replace("(", "\\(").replace(")", "\\)")
        if index:
            commands.append("0 -30 Td")
        commands.append(f"({escaped}) Tj")
    stream = ("\n".join([*commands, "ET"]) + "\n").encode("ascii")
    objects = [
        b"<< /Type /Catalog /Pages 2 0 R >>",
        b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
        b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        f"<< /Length {len(stream)} >>\nstream\n".encode() + stream + b"endstream",
    ]
    result = b"%PDF-1.4\n"
    offsets = [0]
    for index, obj in enumerate(objects, 1):
        offsets.append(len(result))
        result += f"{index} 0 obj\n".encode() + obj + b"\nendobj\n"
    start = len(result)
    result += f"xref\n0 {len(offsets)}\n0000000000 65535 f \n".encode()
    result += b"".join(f"{offset:010d} 00000 n \n".encode() for offset in offsets[1:])
    return result + f"trailer\n<< /Size {len(offsets)} /Root 1 0 R >>\nstartxref\n{start}\n%%EOF\n".encode()


def generate() -> None:
    (ROOT / "assets/notice.pdf").write_bytes(pdf_bytes((ROOT / "assets/notice.txt").read_text()))
    if not shutil.which("say"):
        raise RuntimeError("Audio regeneration needs macOS say; use committed WAV on other OSes.")
    subprocess.run(
        ["say", "-v", "Samantha", "-r", "145", "-f", str(ROOT / "assets/spoken.txt"),
         "-o", str(ROOT / "assets/notice.wav"), "--file-format=WAVE", "--data-format=LEI16@24000"],
        check=True,
    )
    with wave.open(str(ROOT / "assets/notice.wav"), "rb") as audio:
        assert (audio.getnchannels(), audio.getsampwidth(), audio.getframerate()) == (1, 2, 24000)


if __name__ == "__main__":
    generate()
