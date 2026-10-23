# pyrefly: ignore[missing-import]
import cv2

img = cv2.imread('daun_kelor.png')

gray = cv2.cvtColor(img, cv2.COLOR_BGR2GRAY)
hsv = cv2.cvtColor(img, cv2.COLOR_BGR2HSV)
h, s, v = cv2.split(hsv)

bawah = (35, 60, 40)
atas = (85, 255, 255)
mask = cv2.inRange(hsv, bawah, atas)

persen = 100 * cv2.countNonZero(mask) / mask.size
print(f'Proporsi piksel hijau: {persen:.1f}%')

daun_saja = cv2.bitwise_and(img, img, mask=mask)
cv2.imwrite('daun_mask.png', mask)
cv2.imwrite('daun_saja.png', daun_saja)