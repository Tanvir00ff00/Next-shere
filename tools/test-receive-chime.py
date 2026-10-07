"""Guard against the surround-output regression that silenced the mono chime."""
from pathlib import Path
import math
import struct
import wave

path = Path(__file__).resolve().parents[1] / "src/NextShare.App/Assets/nextshare-received.wav"
with wave.open(str(path), "rb") as sound:
    assert sound.getnchannels() == 2, "Mono chimes can be routed to an absent center speaker"
    assert sound.getsampwidth() == 2 and sound.getframerate() == 44100
    assert sound.getnframes() == round(0.78 * 44100)
    frames = list(struct.iter_unpack("<hh", sound.readframes(sound.getnframes())))
assert all(left == right for left, right in frames), "Signature must reach both front speakers equally"
peak = max(abs(left) for left, _ in frames) / 32767
rms = math.sqrt(sum(left * left for left, _ in frames) / len(frames)) / 32767
assert 0.64 < peak < 0.66 and 0.1 < rms < 0.3, "Chime must be audible without clipping"
assert abs(frames[0][0]) < 32 and abs(frames[-1][0]) < 32, "Smooth boundaries prevent clicks"
print(f"PASS stereo signature: {len(frames)} frames, peak {peak:.4f}, RMS {rms:.4f}, identical L/R")
