# pyrefly: ignore[missing-import]
import cv2
import numpy as np
import matplotlib.pyplot as plt

img = cv2.imread('daun_kelor.png')
gray = cv2.cvtColor(img, cv2.COLOR_BGR2GRAY)

def resample(citra, n):
    kecil = cv2.resize(citra, (n, n), interpolation=cv2.INTER_AREA)
    return cv2.resize(kecil, (citra.shape[1], citra.shape[0]), interpolation=cv2.INTER_NEAREST)

def kuantisasi(citra, bit):
    L = 2 ** bit
    q = np.floor(citra.astype(np.float32) / 256 * L)
    return (q * 255 / (L - 1)).astype(np.uint8)

# Efek sampling
fig, ax = plt.subplots(1, 4, figsize=(12, 3))
for a, n in zip(ax, [400, 100, 50, 25]):
    a.imshow(resample(gray, n), cmap='gray')
    a.set_title(f'{n} x {n}')
    a.axis('off')
plt.show()

# Efek kuantisasi
fig, ax = plt.subplots(1, 4, figsize=(12, 3))
for a, b in zip(ax, [8, 4, 2, 1]):
    a.imshow(kuantisasi(gray, b), cmap='gray', vmin=0, vmax=255)
    a.set_title(f'{b} bit')
    a.axis('off')
plt.show()