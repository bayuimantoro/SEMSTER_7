import os
# pyrefly: ignore[missing-import]
import cv2
import numpy as np

img = cv2.imread('daun_kelor.png')

cv2.imwrite('daun.png', img)
cv2.imwrite('daun_q90.jpg', img, [cv2.IMWRITE_JPEG_QUALITY, 90])
cv2.imwrite('daun_q30.jpg', img, [cv2.IMWRITE_JPEG_QUALITY, 30])
cv2.imwrite('daun.bmp', img)

for f in ['daun.bmp', 'daun.png', 'daun_q90.jpg', 'daun_q30.jpg']:
    ukuran = os.path.getsize(f) / 1024
    beda = np.abs(cv2.imread(f).astype(int) - img.astype(int))
    print(f'{f:13s} {ukuran:7.1f} KB selisih={beda.mean():.2f}')