# pyrefly: ignore[missing-import]
import cv2
import matplotlib.pyplot as plt

# Sesuaikan dengan nama file gambar kamu
img = cv2.imread('daun_kelor.png')
if img is None:
    raise FileNotFoundError('Berkas citra tidak ditemukan!')

print('Ukuran (tinggi, lebar, kanal):', img.shape)
print('Tipe data piksel             :', img.dtype)
print('Jumlah piksel                :', img.shape[0] * img.shape[1])

rgb = cv2.cvtColor(img, cv2.COLOR_BGR2RGB)
plt.imshow(rgb)
plt.title('Citra daun (RGB)')
plt.axis('off')
plt.show()