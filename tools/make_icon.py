"""生成 投音通/BluetoothCast 应用图标。

使用 Windows 11 系统蓝牙字形（Segoe MDL2 Assets 的 E702，即设置里的蓝牙/设备图标），
白色字形叠加在 Windows 蓝圆角磁贴上，输出多尺寸 32bpp ICO。

依赖：Pillow，以及系统字体 C:/Windows/Fonts/segmdl2.ttf（Segoe MDL2 Assets）。
"""
import os
from PIL import Image, ImageDraw, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(os.path.dirname(HERE), "Assets", "bluetooth.ico")

FONT = r"C:\Windows\Fonts\segmdl2.ttf"
GLYPH = "\ue702"                       # Bluetooth（Windows 设置里的蓝牙/设备图标）
BG = (0x00, 0x78, 0xD4, 255)           # Windows 蓝
PAD = 0.12                            # 磁贴四周留白（归一化）
RADIUS = 0.22                         # 圆角半径（归一化）
GLYPH_SCALE = 0.74                    # 字形占磁贴的比例
SUPERSAMPLE = 8                       # 超采样倍数，保证小尺寸清晰


def render_tile(size: int) -> Image.Image:
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    p = int(round(PAD * size))
    r = int(round(RADIUS * size))
    d.rounded_rectangle([p, p, size - p, size - p], radius=r, fill=BG)

    # 在高分辨率离屏层绘制字形，再缩小合成，得到抗锯齿的清晰边缘
    big = size * SUPERSAMPLE
    glyph = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    gd = ImageDraw.Draw(glyph)
    font = ImageFont.truetype(FONT, int(round(big * GLYPH_SCALE)))
    gd.text((big / 2, big / 2), GLYPH, font=font,
            fill=(255, 255, 255, 255), anchor="mm")
    glyph = glyph.resize((size, size), Image.LANCZOS)
    return Image.alpha_composite(img, glyph)


def make_ico(path: str) -> None:
    sizes = [16, 24, 32, 48, 64, 128, 256]
    images = [render_tile(s) for s in sizes]
    os.makedirs(os.path.dirname(path), exist_ok=True)
    images[0].save(
        path,
        format="ICO",
        sizes=[(s, s) for s in sizes],
        append_images=images[1:],
    )
    print(f"wrote {path} ({len(images)} sizes)")


if __name__ == "__main__":
    make_ico(OUT)
