# pyrefly: ignore[missing-import]
import cv2

img = cv2.imread('daun_kelor.png')

b, g, r = img[120, 200]
print(f'Piksel (x=200, y=120): R={r}, G={g}, B={b}')

roi = img[100:200, 150:300]
print('Ukuran ROI:', roi.shape)

salinan = img.copy()
salinan[100:200, 150:300] = (0, 0, 255)

print('Rata-rata kanal (B,G,R):', img.mean(axis=(0, 1)).round(1))
print('Nilai minimum dan maksimum:', img.min(), img.max())