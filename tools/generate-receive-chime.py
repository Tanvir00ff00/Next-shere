"""Create Next Share's original three-note glass chime (no sampled/system audio)."""
from pathlib import Path
import math
import struct
import wave

rate = 44100
duration = 0.78
notes = [(0.00, 659.255), (0.095, 987.767), (0.19, 1318.51)]
samples = []
for i in range(round(duration * rate)):
    t = i / rate
    value = 0.0
    for start, frequency in notes:
        age = t - start
        if age < 0:
            continue
        attack = 1 - math.exp(-age / 0.008)
        tail = math.exp(-age / 0.135)
        # Rounded sine body, with quiet glass overtones. Smooth attack avoids clicks.
        body = (math.sin(2 * math.pi * frequency * age)
                + 0.13 * math.sin(2 * math.pi * frequency * 2.01 * age) * math.exp(-age / 0.08)
                + 0.045 * math.sin(2 * math.pi * frequency * 3.98 * age) * math.exp(-age / 0.035))
        value += body * attack * tail
    # Fade to zero at the exact file boundary.
    samples.append(value * min(1, (duration - t) / 0.045))
peak = max(abs(s) for s in samples)
out = Path(__file__).resolve().parents[1] / "src/NextShare.App/Assets/nextshare-received.wav"
out.parent.mkdir(parents=True, exist_ok=True)
with wave.open(str(out), "wb") as sound:
    # Mono was routed to the center speaker on Windows surround outputs.
    # Identical stereo channels preserve the signature and explicitly use front L/R.
    sound.setnchannels(2)
    sound.setsampwidth(2)
    sound.setframerate(rate)
    sound.writeframes(b"".join(struct.pack("<hh", *([round(s / peak * 32767 * 0.65)] * 2)) for s in samples))
print(f"Original chime: {out} ({duration}s, stereo PCM16, peak -3.74 dBFS per channel)")
