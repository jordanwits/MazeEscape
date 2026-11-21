"""Draws Detour's 40 Steam achievement icons (256x256 PNG, <API>_ACHIEVED.png).

One flat line glyph per achievement in the game's menu palette (MenuTheme.cs): bone strokes on the dark
plate, at most one amber accent. Everything is analytic distance-field anti-aliasing, so there are no
gradients, glows or textures. Usage (from the repo root): python SteamPipe/AchievementIcons/make_achievement_icons.py SteamPipe/AchievementIcons
Upload the PNGs on Steamworks > Achievements > Bulk Import/Export with "Automatically generate unachieved
icons" ticked, then publish. Add --sheet to also write a review contact sheet next to the output folder.
"""
import math
import os
import sys

import numpy as np
from PIL import Image, ImageDraw, ImageFont

N = 256          # output size in px
S = 1.45         # px per design unit; the glyph lives in roughly -42..42 units
C = N / 2

def hex_rgb(h):
    return tuple(int(h[i:i + 2], 16) for i in (0, 2, 4))

BG = hex_rgb("171511")      # MenuTheme.Bg
FRAME = hex_rgb("3B362B")   # MenuTheme.Stroke
BONE = hex_rgb("E6E1D3")    # MenuTheme.Bone
AMBER = hex_rgb("D98A4E")   # MenuTheme.AmberBright
FAINT = hex_rgb("6E6759")   # MenuTheme.Faint

W = 8.0    # main stroke, design units
W2 = 5.5   # detail stroke

_yy, _xx = np.mgrid[0:N, 0:N].astype(np.float64) + 0.5


# ------------------------------------------------------------------ geometry helpers (y points down)

def arc(cx, cy, r, a0, a1, n=None):
    """Points on a circle; angles in degrees, 0 = +x, 90 = down."""
    n = n or max(8, int(abs(a1 - a0) / 3))
    return [(cx + r * math.cos(math.radians(a0 + (a1 - a0) * i / n)),
             cy + r * math.sin(math.radians(a0 + (a1 - a0) * i / n))) for i in range(n + 1)]

def ell(cx, cy, rx, ry, a0, a1, n=None):
    n = n or max(8, int(abs(a1 - a0) / 3))
    return [(cx + rx * math.cos(math.radians(a0 + (a1 - a0) * i / n)),
             cy + ry * math.sin(math.radians(a0 + (a1 - a0) * i / n))) for i in range(n + 1)]

def quad(p0, p1, p2, n=28):
    return [((1 - t) ** 2 * p0[0] + 2 * (1 - t) * t * p1[0] + t * t * p2[0],
             (1 - t) ** 2 * p0[1] + 2 * (1 - t) * t * p1[1] + t * t * p2[1])
            for t in (i / n for i in range(n + 1))]

def cubic(p0, p1, p2, p3, n=32):
    return [((1 - t) ** 3 * p0[0] + 3 * (1 - t) ** 2 * t * p1[0] + 3 * (1 - t) * t * t * p2[0] + t ** 3 * p3[0],
             (1 - t) ** 3 * p0[1] + 3 * (1 - t) ** 2 * t * p1[1] + 3 * (1 - t) * t * t * p2[1] + t ** 3 * p3[1])
            for t in (i / n for i in range(n + 1))]

def rrect(x0, y0, x1, y1, r):
    """Closed rounded-rectangle outline (no repeated first point)."""
    if r <= 0:
        return [(x0, y0), (x1, y0), (x1, y1), (x0, y1)]
    return (arc(x1 - r, y0 + r, r, -90, 0, 8) + arc(x1 - r, y1 - r, r, 0, 90, 8)
            + arc(x0 + r, y1 - r, r, 90, 180, 8) + arc(x0 + r, y0 + r, r, 180, 270, 8))

def chevron(tip, direction, length=11, spread=10):
    """Open arrowhead: two strokes meeting at tip, pointing along direction."""
    dx, dy = direction
    m = math.hypot(dx, dy)
    dx, dy = dx / m, dy / m
    nx, ny = -dy, dx
    bx, by = tip[0] - dx * length, tip[1] - dy * length
    return [(bx + nx * spread, by + ny * spread), tip, (bx - nx * spread, by - ny * spread)]


def chaikin(pts, rounds=3):
    """Corner-cutting smoothing of a closed polygon."""
    for _ in range(rounds):
        out = []
        for a, b in zip(pts, pts[1:] + pts[:1]):
            out += [(0.75 * a[0] + 0.25 * b[0], 0.75 * a[1] + 0.25 * b[1]),
                    (0.25 * a[0] + 0.75 * b[0], 0.25 * a[1] + 0.75 * b[1])]
        pts = out
    return pts


# ------------------------------------------------------------------ canvas

class Icon:
    def __init__(self):
        self.img = np.zeros((N, N, 3))
        self.img[:] = BG
        self.rot, self.ox, self.oy = 0.0, 0.0, 0.0

    def place(self, rot=0.0, ox=0.0, oy=0.0):
        """Later shapes are rotated by rot degrees about the origin, then moved by (ox, oy)."""
        self.rot, self.ox, self.oy = rot, ox, oy
        return self

    def px(self, p):
        a = math.radians(self.rot)
        x = p[0] * math.cos(a) - p[1] * math.sin(a) + self.ox
        y = p[0] * math.sin(a) + p[1] * math.cos(a) + self.oy
        return C + x * S, C + y * S

    def _blend(self, cov, col):
        cov = cov[..., None]
        self.img = self.img * (1 - cov) + np.array(col, dtype=np.float64) * cov

    def stroke(self, pts, col=BONE, w=W, closed=False):
        P = [self.px(p) for p in pts]
        if closed:
            P.append(P[0])
        d = np.full((N, N), 1e9)
        if len(P) == 1:
            d = np.hypot(_xx - P[0][0], _yy - P[0][1])
        for (x0, y0), (x1, y1) in zip(P, P[1:]):
            dx, dy = x1 - x0, y1 - y0
            l2 = dx * dx + dy * dy
            if l2 == 0:
                dd = np.hypot(_xx - x0, _yy - y0)
            else:
                t = np.clip(((_xx - x0) * dx + (_yy - y0) * dy) / l2, 0, 1)
                dd = np.hypot(_xx - (x0 + t * dx), _yy - (y0 + t * dy))
            d = np.minimum(d, dd)
        self._blend(np.clip(w * S / 2 - d + 0.5, 0, 1), col)

    def fill(self, pts, col=BONE):
        ss = 8
        m = Image.new("L", (N * ss, N * ss), 0)
        ImageDraw.Draw(m).polygon([(x * ss, y * ss) for x, y in (self.px(p) for p in pts)], fill=255)
        self._blend(np.asarray(m.resize((N, N), Image.BOX), dtype=np.float64) / 255.0, col)

    def disc(self, c, r, col=BONE):
        x, y = self.px(c)
        self._blend(np.clip(r * S - np.hypot(_xx - x, _yy - y) + 0.5, 0, 1), col)

    def circle(self, c, r, col=BONE, w=W):
        x, y = self.px(c)
        d = np.abs(np.hypot(_xx - x, _yy - y) - r * S)
        self._blend(np.clip(w * S / 2 - d + 0.5, 0, 1), col)

    def frame(self):
        self.place()
        self.stroke(rrect(-80, -80, 80, 80, 7), FRAME, 1.5, closed=True)

    def image(self):
        return Image.fromarray(np.clip(self.img + 0.5, 0, 255).astype(np.uint8), "RGB")


def bust(I, cx, cy, s, col=BONE, head=None, w=W):
    """Head-and-shoulders figure centred near (cx, cy); s scales it."""
    I.disc((cx, cy - 12 * s), 10.5 * s, head or col)
    I.stroke(arc(cx, cy + 25 * s, 21 * s, 192, 348), col, w)


# ------------------------------------------------------------------ glyphs

def detour(I):
    for y in range(-14, -40, -12):                      # the blocked straight route
        I.stroke([(-18, y), (-18, y - 5)], FAINT, W2)
    I.stroke([(-18, 40), (-18, 4), (18, 4), (18, -30)])
    I.stroke(chevron((18, -38), (0, -1)), AMBER)

def step_right_up(I):
    I.stroke(quad((0, -28), (-12, -14), (-38, -6)))
    I.stroke(quad((0, -28), (12, -14), (38, -6)))
    I.stroke([(-38, -6), (38, -6)])
    I.stroke([(-30, -6), (-30, 34)])
    I.stroke([(30, -6), (30, 34)])
    I.stroke([(-40, 34), (40, 34)])
    I.stroke([(-11, 34), (0, 10), (11, 34)])
    I.stroke([(0, -28), (0, -42)], BONE, W2)
    I.fill([(1, -46), (17, -41.5), (1, -37)], AMBER)

def clocking_out(I):
    I.circle((0, 0), 36)
    for a in (-90, 0, 90, 180):
        c, s = math.cos(math.radians(a)), math.sin(math.radians(a))
        I.stroke([(26 * c, 26 * s), (21 * c, 21 * s)], BONE, W2)
    I.stroke([(0, 0), (0, -21)], BONE, W2)
    I.stroke([(0, 0), (16 * math.cos(math.radians(60)), 16 * math.sin(math.radians(60)))], AMBER)
    I.disc((0, 0), 4.5)

def perfect_attendance(I):
    I.stroke(rrect(-28, -32, 28, 40, 5), closed=True)
    I.fill(rrect(-13, -40, 13, -25, 3))
    for y in (-8, 9, 26):
        I.stroke([(-18, y), (-13, y + 5), (-5, y - 5)], AMBER, W2)
        I.stroke([(3, y), (17, y)], BONE, W2)

def lone_survivor(I):
    bust(I, 0, 2, 1.45, BONE, AMBER)

def full_crew(I):
    for x in (-19, 19):
        for y in (-15, 23):
            bust(I, x, y, 0.76, BONE, w=W2 + 0.5)

def half_bone(I, rot, ox, oy):
    """Half a bone: knob end at local x=-34, a jagged snap at x=0."""
    I.place(rot, ox, oy)
    I.fill([(-30, -5), (-4, -5), (0, -1.5), (-3.5, 1.5), (0.5, 5), (-30, 5)])
    I.disc((-33, -5.5), 7)
    I.disc((-33, 5.5), 7)
    I.place()


def rock_beats_bones(I):
    base, kink, gap = -35, 9, 4
    d = (math.cos(math.radians(base)), math.sin(math.radians(base)))
    half_bone(I, base + kink, -gap * d[0], 8 - gap * d[1])
    half_bone(I, base + 180 - kink, gap * d[0], 8 + gap * d[1])
    for a in (-145, -112, -79):
        c, s = math.cos(math.radians(a)), math.sin(math.radians(a))
        I.stroke([(16 * c, 4 + 16 * s), (25 * c, 4 + 25 * s)], AMBER, W2)

def jailbreak(I):
    I.stroke([(-32, -36), (32, -36)])
    I.stroke([(-32, 36), (32, 36)])
    for x in (-27, 27):
        I.stroke([(x, -36), (x, 36)])
    I.stroke(quad((-9, -36), (-26, 0), (-9, 36)), AMBER)
    I.stroke(quad((9, -36), (26, 0), (9, 36)), AMBER)

def special_delivery(I):
    I.stroke(rrect(-30, -16, 30, 36, 3), closed=True)
    I.stroke(rrect(-35, -29, 35, -16, 3), closed=True)
    I.stroke([(0, -29), (0, 36)], AMBER, W2)
    I.circle((-6.5, -36), 5.5, AMBER, W2)
    I.circle((6.5, -36), 5.5, AMBER, W2)

def bone_collector(I):
    I.stroke(arc(0, -6, 30, 130, 410))
    jaw_y = -6 + 30 * math.sin(math.radians(130))
    jaw_x = 30 * math.cos(math.radians(50))
    I.stroke([(-jaw_x, jaw_y), (-14, 34), (14, 34), (jaw_x, jaw_y)])
    I.stroke([(-5, 26), (-5, 34)], BONE, W2)
    I.stroke([(5, 26), (5, 34)], BONE, W2)
    I.disc((-11.5, -3), 7.5)
    I.disc((11.5, -3), 7.5)
    I.fill([(0, 8), (-4.5, 16), (4.5, 16)])

def poise_breaker(I):
    shield = ([(-28, -32), (28, -32), (28, 2)] + quad((28, 2), (26, 28), (0, 40))[1:]
              + quad((0, 40), (-26, 28), (-28, 2))[1:])
    I.stroke(shield, closed=True)
    I.stroke([(6, -32), (-4, -14), (7, -2), (-3, 14), (1, 24)], AMBER, W2)

def close_shave(I):
    I.stroke(arc(-36, 0, 52, -62, -40), FAINT, W2)
    I.stroke(arc(-36, 0, 40, -60, -42), FAINT, W2)
    I.place(30, -9, 6)
    I.stroke([(0, -30), (0, 40)])
    I.fill([(-6, -38), (4, -38)] + arc(8, -30, 20, -50, 50) + [(4, -22), (-6, -22)], AMBER)
    I.place()

def send_in_the_clowns(I):
    I.circle((0, 0), 34)
    for cx in (-13, 13):
        I.stroke([(cx - 5, -15), (cx + 5, -5)], BONE, W2)
        I.stroke([(cx - 5, -5), (cx + 5, -15)], BONE, W2)
    I.stroke(arc(0, 3, 21, 32, 148), BONE, W2)
    I.disc((0, 3), 8, AMBER)

def encore(I):
    a1 = 250
    I.stroke(arc(0, 0, 31, -38, a1))
    end = (31 * math.cos(math.radians(a1)), 31 * math.sin(math.radians(a1)))
    tangent = (-math.sin(math.radians(a1)), math.cos(math.radians(a1)))
    tip = (end[0] + tangent[0] * 4, end[1] + tangent[1] * 4)
    I.stroke(chevron(tip, tangent, 11, 10))
    I.disc((0, 0), 10, AMBER)

def heart(I, cx, cy, s, col):
    I.disc((cx - 5 * s, cy - 2.5 * s), 5.6 * s, col)
    I.disc((cx + 5 * s, cy - 2.5 * s), 5.6 * s, col)
    I.fill([(cx - 10.4 * s, cy - 0.6 * s), (cx + 10.4 * s, cy - 0.6 * s), (cx, cy + 11 * s)], col)

def winner_winner(I):
    card = rrect(-17, -24, 17, 24, 4)
    I.place(-14, -10, 0)
    I.fill(card, BG)
    I.stroke(card, closed=True)
    I.place(10, 10, 2)
    I.fill(card, BG)
    I.stroke(card, closed=True)
    heart(I, 0, -1, 1.05, AMBER)
    I.place()

def blackjack(I):
    I.stroke(rrect(-25, -35, 25, 35, 5), closed=True)
    # spade: an upside-down heart with a flared stem
    I.disc((-7.5, 4), 8.2, AMBER)
    I.disc((7.5, 4), 8.2, AMBER)
    I.fill([(-15.2, 1.5), (15.2, 1.5), (0, -19)], AMBER)
    I.fill([(0, 6), (-7, 20), (7, 20)], AMBER)

def high_roller(I):
    I.place(-12)
    outline = ([(-38, -20), (38, -20), (38, -8)] + arc(38, 0, 8, 270, 90)[1:-1]
               + [(38, 8), (38, 20), (-38, 20), (-38, 8)] + arc(-38, 0, 8, 90, -90)[1:-1] + [(-38, -8)])
    I.stroke(outline, closed=True)
    for y in (-11, -3.7, 3.7, 11):
        I.disc((16, y), 2.4, AMBER)
    I.place()

def retail_therapy(I):
    I.place(-35, 2, 0)
    I.stroke([(-36, 0), (-19, -19), (36, -19), (36, 19), (-19, 19)], closed=True)
    I.circle((-15, 0), 5, AMBER, W2)
    I.place()

def monkey_business(I):
    I.stroke(ell(-13, -24, 12, 9.5, 0, 360), BONE, W, closed=True)
    I.stroke(ell(13, -24, 12, 9.5, 0, 360), BONE, W, closed=True)
    I.stroke([(0, -24), (0, 36)])
    I.stroke([(-10, 8), (10, 8)], AMBER)
    I.stroke([(-6, 36), (6, 36)], BONE, W2)

def dont_blink(I):
    I.stroke(quad((-39, 0), (0, -33), (39, 0)))
    I.stroke(quad((-39, 0), (0, 33), (39, 0)))
    I.circle((0, 0), 12.5)
    I.disc((0, 0), 5.5, AMBER)

def wrong_target(I):
    I.circle((-6, 8), 27)
    I.place(45, 15, -13)
    I.fill(rrect(-8, -6, 8, 6, 2), BG)
    I.stroke(rrect(-8, -6, 8, 6, 2), BONE, W2, closed=True)
    I.place()
    I.stroke(quad((20, -18), (24, -32), (34, -33)), BONE, W2)
    for a in (-90, -18, 54, 126, 198):
        c, s = math.cos(math.radians(a)), math.sin(math.radians(a))
        I.stroke([(36 + 4 * c, -35 + 4 * s), (36 + 9 * c, -35 + 9 * s)], AMBER, W2 * 0.8)

def nothing_but_net(I):
    I.stroke(rrect(-32, -40, 32, -14, 3), closed=True)
    I.stroke(rrect(-10, -31, 10, -14, 1), BONE, W2, closed=True)
    I.stroke([(-22, -8), (-14, 30)], BONE, W2)
    I.stroke([(22, -8), (14, 30)], BONE, W2)
    I.stroke([(-22, -8), (14, 30)], BONE, W2)
    I.stroke([(22, -8), (-14, 30)], BONE, W2)
    I.stroke([(-24, -8), (24, -8)], AMBER)

def limbo(I):
    I.stroke([(-34, -34), (-34, 36)])
    I.stroke([(34, -34), (34, 36)])
    I.stroke([(-34, -8), (34, -8)], AMBER, W2)
    I.stroke([(-20, 20), (18, 20)])
    I.stroke(chevron((20, 20), (1, 0), 10, 9))

def security_breach(I):
    I.stroke([(-6, -40), (26, -40)])
    I.stroke([(10, -40), (10, -18)])
    I.place(22, 0, 2)
    body = rrect(-24, -12, 24, 12, 4)
    I.fill(body, BG)
    I.stroke(body, closed=True)
    I.stroke([(-24, -12), (-36, -16), (-36, 16), (-24, 12)], closed=True)
    I.disc((13, 0), 4, AMBER)
    I.place()

def the_final_boss(I):
    I.stroke([(-34, 26), (-36, -14), (-17, 4), (0, -22), (17, 4), (36, -14), (34, 26)], closed=True)
    I.disc((0, -30), 5, AMBER)
    I.disc((-38, -22), 4, AMBER)
    I.disc((38, -22), 4, AMBER)

def disco_inferno(I):
    cy = 6
    cell = [(0, -6)] + ell(0, cy, 14, 28, -25.4, 0)[1:] + [(0, cy)]
    I.fill(cell, AMBER)
    I.stroke([(0, -44), (0, -22)], BONE, W2)
    for y in (-6, 18):
        half = math.sqrt(28 ** 2 - (y - cy) ** 2)
        I.stroke([(-half, y), (half, y)], BONE, W2)
    I.stroke([(-28, cy), (28, cy)], BONE, W2)
    I.stroke([(0, cy - 28), (0, cy + 28)], BONE, W2)
    I.stroke(ell(0, cy, 14, 28, 90, 270), BONE, W2)
    I.stroke(ell(0, cy, 14, 28, -90, 90), BONE, W2)
    I.circle((0, cy), 28)

def touch_grass(I):
    I.stroke(quad((-9, 34), (-12, 2), (-31, -10)))
    I.stroke(quad((9, 34), (13, 6), (29, -6)))
    I.stroke(quad((0, 34), (-3, -6), (6, -36)), AMBER)
    I.stroke([(-38, 34), (38, 34)])

def look_away(I):
    I.stroke(quad((-34, 6), (0, 32), (34, 6)))
    for x, a in ((-22, 112), (-8, 98), (8, 82), (22, 68)):
        t = 0.5 + x / 68
        y = (1 - t) ** 2 * 6 + 2 * (1 - t) * t * 32 + t * t * 6
        c, s = math.cos(math.radians(a)), math.sin(math.radians(a))
        I.stroke([(x, y), (x + 8 * c, y + 8 * s)], BONE, W2)
    for a in (-160, -125, -90, -55, -20):
        c, s = math.cos(math.radians(a)), math.sin(math.radians(a))
        I.stroke([(16 * c, 2 + 16 * s), (28 * c, 2 + 28 * s)], AMBER, W2)

def friendly_fire(I):
    bust(I, 0, 12, 1.25)
    hx, hy = 0, 12 - 15
    for a in (-160, -125, -90, -55, -20):
        c, s = math.cos(math.radians(a)), math.sin(math.radians(a))
        I.stroke([(hx + 20 * c, hy + 20 * s), (hx + 28 * c, hy + 28 * s)], AMBER, W2)

def signal_fire(I):
    outer = (cubic((0, 38), (-26, 38), (-31, 10), (-15, -6))
             + cubic((-15, -6), (-7, -15), (-1, -26), (5, -39))[1:]
             + cubic((5, -39), (19, -22), (31, -2), (25, 17))[1:]
             + cubic((25, 17), (21, 31), (11, 38), (0, 38))[1:])
    I.stroke(outer, closed=True)
    inner = (cubic((0, 30), (-11, 30), (-13, 17), (-6, 8))
             + cubic((-6, 8), (-2, 2), (0, -3), (2, -9))[1:]
             + cubic((2, -9), (10, 1), (13, 12), (10, 21))[1:]
             + cubic((10, 21), (8, 27), (4, 30), (0, 30))[1:])
    I.fill(inner, AMBER)

def blade_runner(I):
    I.place(45)
    I.fill([(-5.5, 5), (5.5, 5), (5.5, -30), (0, -42), (-5.5, -30)])
    I.stroke([(-17, 9), (17, 9)], AMBER)
    I.stroke([(0, 13), (0, 27)])
    I.disc((0, 34), 6)
    I.place()

def field_medic(I):
    I.place(-40)
    I.stroke(rrect(-42, -14, 42, 14, 14), closed=True)
    I.stroke([(-15, -14), (-15, 14)], BONE, W2)
    I.stroke([(15, -14), (15, 14)], BONE, W2)
    for x in (-5.5, 5.5):
        for y in (-5, 5):
            I.disc((x, y), 2.6, AMBER)
    I.place()

def wired(I):
    rx, ry = 20, 6
    I.stroke(ell(0, -28, rx, ry, 0, 360), BONE, W2 + 0.5, closed=True)
    I.stroke([(-rx, -28), (-rx, 30)])
    I.stroke([(rx, -28), (rx, 30)])
    I.stroke(ell(0, 30, rx, ry, 0, 180))
    I.fill([(5, -12), (-10, 8), (-1, 8), (-5, 25), (10, 3), (1, 3)], AMBER)

def now_you_see_me(I):
    I.circle((0, 0), 34)
    turns = 2.2
    tmax = turns * 2 * math.pi
    pts = [((3 + 21 * t / tmax) * math.cos(t - math.pi / 2), (3 + 21 * t / tmax) * math.sin(t - math.pi / 2))
           for t in (tmax * i / 220 for i in range(221))]
    I.stroke(pts, AMBER, W2)

def wipe(I):
    I.stroke([(-24, 34), (-24, -10)] + arc(0, -10, 24, 180, 360)[1:] + [(24, 34)])
    I.stroke([(-38, 34), (38, 34)])
    I.stroke([(0, -18), (0, 14)], AMBER, W2)
    I.stroke([(-9, -8), (9, -8)], AMBER, W2)

def no_way_out(I):
    I.stroke([(-15, -6), (-15, -18)] + arc(0, -18, 15, 180, 360)[1:] + [(15, -6)])
    I.fill(rrect(-26, -6, 26, 36, 6), BG)
    I.stroke(rrect(-26, -6, 26, 36, 6), closed=True)
    I.disc((0, 10), 5, AMBER)
    I.fill([(-2.6, 11), (2.6, 11), (3.6, 24), (-3.6, 24)], AMBER)

def last_one_standing(I):
    for i, (x, y) in enumerate(((-19, -15), (19, -15), (-19, 23), (19, 23))):
        if i == 1:
            bust(I, x, y, 0.76, BONE, AMBER, w=W2 + 0.5)
        else:
            bust(I, x, y, 0.76, FAINT, w=W2 + 0.5)

def pacifist(I):
    d = 34 * math.cos(math.radians(45))
    I.stroke([(0, -34), (0, 34)], AMBER)
    I.stroke([(0, 0), (-d, d)], AMBER)
    I.stroke([(0, 0), (d, d)], AMBER)
    I.circle((0, 0), 34)

def ghost(I):
    oy = -5
    pts = [(-26, 34 + oy), (-26, -8 + oy)] + arc(0, -8 + oy, 26, 180, 360)[1:] + [(26, 34 + oy)]
    w = 52 / 3
    for k in range(3):
        cx = 26 - w / 2 - k * w
        pts += arc(cx, 34 + oy, w / 2, 0, 180)[1:]
    I.stroke(pts, closed=False)
    I.disc((-9, -6 + oy), 4.5)
    I.disc((9, -6 + oy), 4.5)

def speedrunner(I):
    cy = 8
    I.circle((0, cy), 30)
    I.stroke([(0, cy - 30), (0, cy - 38)])
    I.stroke([(-8, cy - 42), (8, cy - 42)])
    a = math.radians(-45)
    I.stroke([(35 * math.cos(a), cy + 35 * math.sin(a)), (41 * math.cos(a), cy + 41 * math.sin(a))], BONE, W2)
    I.stroke([(0, cy), (17 * math.cos(a), cy + 17 * math.sin(a))], AMBER)
    I.disc((0, cy), 4)


GLYPHS = [
    ("ACH_DETOUR", detour), ("ACH_STEP_RIGHT_UP", step_right_up), ("ACH_CLOCKING_OUT", clocking_out),
    ("ACH_PERFECT_ATTENDANCE", perfect_attendance), ("ACH_LONE_SURVIVOR", lone_survivor),
    ("ACH_FULL_CREW", full_crew), ("ACH_ROCK_BEATS_BONES", rock_beats_bones), ("ACH_JAILBREAK", jailbreak),
    ("ACH_SPECIAL_DELIVERY", special_delivery), ("ACH_BONE_COLLECTOR", bone_collector),
    ("ACH_POISE_BREAKER", poise_breaker), ("ACH_CLOSE_SHAVE", close_shave),
    ("ACH_SEND_IN_THE_CLOWNS", send_in_the_clowns), ("ACH_ENCORE", encore),
    ("ACH_WINNER_WINNER", winner_winner), ("ACH_BLACKJACK", blackjack), ("ACH_HIGH_ROLLER", high_roller),
    ("ACH_RETAIL_THERAPY", retail_therapy), ("ACH_MONKEY_BUSINESS", monkey_business),
    ("ACH_DONT_BLINK", dont_blink), ("ACH_WRONG_TARGET", wrong_target), ("ACH_NOTHING_BUT_NET", nothing_but_net),
    ("ACH_LIMBO", limbo), ("ACH_SECURITY_BREACH", security_breach), ("ACH_THE_FINAL_BOSS", the_final_boss),
    ("ACH_DISCO_INFERNO", disco_inferno), ("ACH_TOUCH_GRASS", touch_grass), ("ACH_LOOK_AWAY", look_away),
    ("ACH_FRIENDLY_FIRE", friendly_fire), ("ACH_SIGNAL_FIRE", signal_fire), ("ACH_BLADE_RUNNER", blade_runner),
    ("ACH_FIELD_MEDIC", field_medic), ("ACH_WIRED", wired), ("ACH_NOW_YOU_SEE_ME", now_you_see_me),
    ("ACH_WIPE", wipe), ("ACH_NO_WAY_OUT", no_way_out), ("ACH_LAST_ONE_STANDING", last_one_standing),
    ("ACH_PACIFIST", pacifist), ("ACH_GHOST", ghost), ("ACH_SPEEDRUNNER", speedrunner),
]


def render(fn):
    I = Icon()
    I.frame()
    fn(I)
    return I.image()


def contact_sheet(images, path, cell=150, icon=128, small=64):
    cols = 8
    rows = math.ceil(len(images) / cols)
    sheet = Image.new("RGB", (cols * cell, rows * (cell + small + 10)), (40, 40, 40))
    try:
        font = ImageFont.truetype("bahnschrift.ttf", 11)
    except OSError:
        font = ImageFont.load_default()
    d = ImageDraw.Draw(sheet)
    for i, (name, im) in enumerate(images):
        x, y = (i % cols) * cell, (i // cols) * (cell + small + 10)
        sheet.paste(im.resize((icon, icon), Image.LANCZOS), (x + (cell - icon) // 2, y + 4))
        sheet.paste(im.resize((small, small), Image.LANCZOS), (x + (cell - small) // 2, y + icon + 8))
        d.text((x + 6, y + icon + small + 10), name[4:], fill=(200, 200, 200), font=font)
    sheet.save(path)


if __name__ == "__main__":
    out = sys.argv[1]
    os.makedirs(out, exist_ok=True)
    rendered = []
    for name, fn in GLYPHS:
        im = render(fn)
        im.save(os.path.join(out, f"{name}_ACHIEVED.png"))
        rendered.append((name, im))
    if "--sheet" in sys.argv:
        contact_sheet(rendered, os.path.join(os.path.dirname(out.rstrip("/\\")) or ".", "contact_sheet.png"))
    print(len(rendered), "icons written to", out)
