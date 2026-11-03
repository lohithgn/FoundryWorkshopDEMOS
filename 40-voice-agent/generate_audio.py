"""Regenerate the benign spoken fixture locally on macOS, without cloud credentials."""

from pathlib import Path
import shutil
import subprocess
import wave

ROOT = Path(__file__).resolve().parent


def generate() -> None:
    if not shutil.which("say"):
        raise RuntimeError("Regeneration needs macOS say; the committed WAV works on other OSes.")
    subprocess.run(
        ["say", "-v", "Samantha", "-r", "145", "-f", str(ROOT / "assets/turn.txt"),
         "-o", str(ROOT / "assets/turn.wav"), "--file-format=WAVE", "--data-format=LEI16@24000"],
        check=True,
    )
    with wave.open(str(ROOT / "assets/turn.wav"), "rb") as audio:
        assert (audio.getnchannels(), audio.getsampwidth(), audio.getframerate()) == (1, 2, 24000)


if __name__ == "__main__":
    generate()
