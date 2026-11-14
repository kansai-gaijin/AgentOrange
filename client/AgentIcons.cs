using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace AgentOrange;

/// <summary>
/// The line icons T3 Code draws, from Lucide (lucide-static 0.460.0, ISC licence: Lucide
/// Contributors, and Feather, MIT, Cole Bemis), as vector paths on a 24 unit grid, and its
/// Claude and OpenAI marks. Made from the SVGs by a script; each draws at any size.
/// </summary>
public static class AgentIcons
{
    private static readonly Dictionary<string, Geometry> Lines = new()
    {
        ["git-fork"] = Geometry.Parse("M 9 18 a 3 3 0 1 0 6 0 a 3 3 0 1 0 -6 0 M 3 6 a 3 3 0 1 0 6 0 a 3 3 0 1 0 -6 0 M 15 6 a 3 3 0 1 0 6 0 a 3 3 0 1 0 -6 0 M 18 9 v 2 c 0 .6 -.4 1 -1 1 H 7 c -.6 0 -1 -.4 -1 -1 V 9 M 12 12 v 3"),
        ["maximize-2"] = Geometry.Parse("M 15 3 h 6 v 6 M 21 3 l -7 7 M 3 21 l 7 -7 M 9 21 H 3 v -6"),
        ["minimize-2"] = Geometry.Parse("M 14 10 l 7 -7 M 20 10 h -6 V 4 M 3 21 l 7 -7 M 4 14 h 6 v 6"),
        ["pilcrow"] = Geometry.Parse("M 13 4 v 16 M 17 4 v 16 M 19 4 H 9.5 a 4.5 4.5 0 0 0 0 9 H 13"),
        ["file-plus"] = Geometry.Parse("M 15 2 H 6 a 2 2 0 0 0 -2 2 v 16 a 2 2 0 0 0 2 2 h 12 a 2 2 0 0 0 2 -2 V 7 Z M 14 2 v 4 a 2 2 0 0 0 2 2 h 4 M 9 15 h 6 M 12 18 v -6"),
        ["file-minus"] = Geometry.Parse("M 15 2 H 6 a 2 2 0 0 0 -2 2 v 16 a 2 2 0 0 0 2 2 h 12 a 2 2 0 0 0 2 -2 V 7 Z M 14 2 v 4 a 2 2 0 0 0 2 2 h 4 M 9 15 h 6"),
        ["file-symlink"] = Geometry.Parse("M 4 11 V 4 a 2 2 0 0 1 2 -2 h 9 l 5 5 v 13 a 2 2 0 0 1 -2 2 h -5 M 14 2 v 4 a 2 2 0 0 0 2 2 h 4 M 10 18 l 3 -3 -3 -3 M 4 15 h 9"),
        ["panel-left"] = Geometry.Parse("M 5 3 h 14 a 2 2 0 0 1 2 2 v 14 a 2 2 0 0 1 -2 2 h -14 a 2 2 0 0 1 -2 -2 v -14 a 2 2 0 0 1 2 -2 Z M 9 3 v 18"),
        ["panel-left-close"] = Geometry.Parse("M 5 3 h 14 a 2 2 0 0 1 2 2 v 14 a 2 2 0 0 1 -2 2 h -14 a 2 2 0 0 1 -2 -2 v -14 a 2 2 0 0 1 2 -2 Z M 9 3 v 18 M 16 15 l -3 -3 3 -3"),
        ["chevron-left"] = Geometry.Parse("M 15 18 l -6 -6 6 -6"),
        ["chevrons-up-down"] = Geometry.Parse("M 7 15 l 5 5 5 -5 M 7 9 l 5 -5 5 5"),
        ["chevrons-down-up"] = Geometry.Parse("M 7 20 l 5 -5 5 5 M 7 4 l 5 5 5 -5"),
        ["text-wrap"] = Geometry.Parse("M 3 6 h 18 M 3 12 h 15 a 3 3 0 1 1 0 6 h -4 M 16 16 l -2 2 2 2 M 3 18 h 7"),
        ["circle-dot-dashed"] = Geometry.Parse("M 10.1 2.18 a 9.93 9.93 0 0 1 3.8 0 M 17.6 3.71 a 9.95 9.95 0 0 1 2.69 2.7 M 21.82 10.1 a 9.93 9.93 0 0 1 0 3.8 M 20.29 17.6 a 9.95 9.95 0 0 1 -2.7 2.69 M 13.9 21.82 a 9.94 9.94 0 0 1 -3.8 0 M 6.4 20.29 a 9.95 9.95 0 0 1 -2.69 -2.7 M 2.18 13.9 a 9.93 9.93 0 0 1 0 -3.8 M 3.71 6.4 a 9.95 9.95 0 0 1 2.7 -2.69 M 11 12 a 1 1 0 1 0 2 0 a 1 1 0 1 0 -2 0"),
        ["undo-2"] = Geometry.Parse("M 9 14 L 4 9 l 5 -5 M 4 9 h 10.5 a 5.5 5.5 0 0 1 5.5 5.5 a 5.5 5.5 0 0 1 -5.5 5.5 H 11"),
        ["cloud-download"] = Geometry.Parse("M 12 13 v 8 l -4 -4 M 12 21 l 4 -4 M 4.393 15.269 A 7 7 0 1 1 15.71 8 h 1.79 a 4.5 4.5 0 0 1 2.436 8.284"),
        ["cloud-upload"] = Geometry.Parse("M 12 13 v 8 M 4 14.899 A 7 7 0 1 1 15.71 8 h 1.79 a 4.5 4.5 0 0 1 2.5 8.242 M 8 17 l 4 -4 4 4"),
        ["file-diff"] = Geometry.Parse("M 15 2 H 6 a 2 2 0 0 0 -2 2 v 16 a 2 2 0 0 0 2 2 h 12 a 2 2 0 0 0 2 -2 V 7 Z M 9 10 h 6 M 12 13 V 7 M 9 17 h 6"),
        ["folder-closed"] = Geometry.Parse("M 20 20 a 2 2 0 0 0 2 -2 V 8 a 2 2 0 0 0 -2 -2 h -7.9 a 2 2 0 0 1 -1.69 -.9 L 9.6 3.9 A 2 2 0 0 0 7.93 3 H 4 a 2 2 0 0 0 -2 2 v 13 a 2 2 0 0 0 2 2 Z M 2 10 h 20"),
        ["git-branch-plus"] = Geometry.Parse("M 6 3 v 12 M 15 6 a 3 3 0 1 0 6 0 a 3 3 0 1 0 -6 0 M 3 18 a 3 3 0 1 0 6 0 a 3 3 0 1 0 -6 0 M 15 6 a 9 9 0 0 0 -9 9 M 18 15 v 6 M 21 18 h -6"),
        ["git-pull-request-arrow"] = Geometry.Parse("M 3 6 a 3 3 0 1 0 6 0 a 3 3 0 1 0 -6 0 M 6 9 v 12 M 15 18 a 3 3 0 1 0 6 0 a 3 3 0 1 0 -6 0 M 11 6 h 5 a 2 2 0 0 1 2 2 v 7 M 14 9 l -3 -3 3 -3"),
        ["loader-circle"] = Geometry.Parse("M 21 12 a 9 9 0 1 1 -6.219 -8.56"),
        ["server"] = Geometry.Parse("M 4 2 h 16 a 2 2 0 0 1 2 2 v 4 a 2 2 0 0 1 -2 2 h -16 a 2 2 0 0 1 -2 -2 v -4 a 2 2 0 0 1 2 -2 Z M 4 14 h 16 a 2 2 0 0 1 2 2 v 4 a 2 2 0 0 1 -2 2 h -16 a 2 2 0 0 1 -2 -2 v -4 a 2 2 0 0 1 2 -2 Z M 6 6 h .01 M 6 18 h .01"),
        ["square-arrow-out-up-right"] = Geometry.Parse("M 21 13 v 6 a 2 2 0 0 1 -2 2 H 5 a 2 2 0 0 1 -2 -2 V 5 a 2 2 0 0 1 2 -2 h 6 M 21 3 l -9 9 M 15 3 h 6 v 6"),
        ["square-menu"] = Geometry.Parse("M 5 3 h 14 a 2 2 0 0 1 2 2 v 14 a 2 2 0 0 1 -2 2 h -14 a 2 2 0 0 1 -2 -2 v -14 a 2 2 0 0 1 2 -2 Z M 7 8 h 10 M 7 12 h 10 M 7 16 h 10"),
        ["alarm-clock"] = Geometry.Parse("M 4 13 a 8 8 0 1 0 16 0 a 8 8 0 1 0 -16 0 M 12 9 v 4 l 2 2 M 5 3 2 6 M 22 6 l -3 -3 M 6.38 18.7 4 21 M 17.64 18.67 20 21"),
        ["arrow-down-to-line"] = Geometry.Parse("M 12 17 V 3 M 6 11 l 6 6 6 -6 M 19 21 H 5"),
        ["arrow-up-from-line"] = Geometry.Parse("M 18 9 l -6 -6 -6 6 M 12 3 v 14 M 5 21 h 14"),
        ["arrow-right-left"] = Geometry.Parse("M 16 3 l 4 4 -4 4 M 20 7 H 4 M 8 21 l -4 -4 4 -4 M 4 17 h 16"),
        ["arrow-right"] = Geometry.Parse("M 5 12 h 14 M 12 5 l 7 7 -7 7"),
        ["arrow-up"] = Geometry.Parse("M 5 12 l 7 -7 7 7 M 12 19 V 5"),
        ["bot"] = Geometry.Parse("M 12 8 V 4 H 8 M 6 8 h 12 a 2 2 0 0 1 2 2 v 8 a 2 2 0 0 1 -2 2 h -12 a 2 2 0 0 1 -2 -2 v -8 a 2 2 0 0 1 2 -2 Z M 2 14 h 2 M 20 14 h 2 M 15 13 v 2 M 9 13 v 2"),
        ["brain"] = Geometry.Parse("M 12 5 a 3 3 0 1 0 -5.997 .125 4 4 0 0 0 -2.526 5.77 4 4 0 0 0 .556 6.588 A 4 4 0 1 0 12 18 Z M 12 5 a 3 3 0 1 1 5.997 .125 4 4 0 0 1 2.526 5.77 4 4 0 0 1 -.556 6.588 A 4 4 0 1 1 12 18 Z M 15 13 a 4.5 4.5 0 0 1 -3 -4 4.5 4.5 0 0 1 -3 4 M 17.599 6.5 a 3 3 0 0 0 .399 -1.375 M 6.003 5.125 A 3 3 0 0 0 6.401 6.5 M 3.477 10.896 a 4 4 0 0 1 .585 -.396 M 19.938 10.5 a 4 4 0 0 1 .585 .396 M 6 18 a 4 4 0 0 1 -1.967 -.516 M 19.967 17.484 A 4 4 0 0 1 18 18"),
        ["camera"] = Geometry.Parse("M 14.5 4 h -5 L 7 7 H 4 a 2 2 0 0 0 -2 2 v 9 a 2 2 0 0 0 2 2 h 16 a 2 2 0 0 0 2 -2 V 9 a 2 2 0 0 0 -2 -2 h -3 l -2.5 -3 z M 9 13 a 3 3 0 1 0 6 0 a 3 3 0 1 0 -6 0"),
        ["chart-no-axes-column"] = Geometry.Parse("M 18 20 L 18 10 M 12 20 L 12 4 M 6 20 L 6 14"),
        ["check"] = Geometry.Parse("M 20 6 9 17 l -5 -5"),
        ["chevron-down"] = Geometry.Parse("M 6 9 l 6 6 6 -6"),
        ["chevron-up"] = Geometry.Parse("M 18 15 l -6 -6 -6 6"),
        ["chevron-right"] = Geometry.Parse("M 9 18 l 6 -6 -6 -6"),
        ["circle-alert"] = Geometry.Parse("M 2 12 a 10 10 0 1 0 20 0 a 10 10 0 1 0 -20 0 M 12 8 L 12 12 M 12 16 L 12.01 16"),
        ["circle-check"] = Geometry.Parse("M 2 12 a 10 10 0 1 0 20 0 a 10 10 0 1 0 -20 0 M 9 12 l 2 2 4 -4"),
        ["circle-dashed"] = Geometry.Parse("M 10.1 2.182 a 10 10 0 0 1 3.8 0 M 13.9 21.818 a 10 10 0 0 1 -3.8 0 M 17.609 3.721 a 10 10 0 0 1 2.69 2.7 M 2.182 13.9 a 10 10 0 0 1 0 -3.8 M 20.279 17.609 a 10 10 0 0 1 -2.7 2.69 M 21.818 10.1 a 10 10 0 0 1 0 3.8 M 3.721 6.391 a 10 10 0 0 1 2.7 -2.69 M 6.391 20.279 a 10 10 0 0 1 -2.69 -2.7"),
        ["circle-x"] = Geometry.Parse("M 2 12 a 10 10 0 1 0 20 0 a 10 10 0 1 0 -20 0 M 15 9 l -6 6 M 9 9 l 6 6"),
        ["circle"] = Geometry.Parse("M 2 12 a 10 10 0 1 0 20 0 a 10 10 0 1 0 -20 0"),
        ["clock"] = Geometry.Parse("M 2 12 a 10 10 0 1 0 20 0 a 10 10 0 1 0 -20 0 M 12 6 L 12 12 L 16 14"),
        ["copy"] = Geometry.Parse("M 10 8 h 10 a 2 2 0 0 1 2 2 v 10 a 2 2 0 0 1 -2 2 h -10 a 2 2 0 0 1 -2 -2 v -10 a 2 2 0 0 1 2 -2 Z M 4 16 c -1.1 0 -2 -.9 -2 -2 V 4 c 0 -1.1 .9 -2 2 -2 h 10 c 1.1 0 2 .9 2 2"),
        ["eye"] = Geometry.Parse("M 2.062 12.348 a 1 1 0 0 1 0 -.696 10.75 10.75 0 0 1 19.876 0 1 1 0 0 1 0 .696 10.75 10.75 0 0 1 -19.876 0 M 9 12 a 3 3 0 1 0 6 0 a 3 3 0 1 0 -6 0"),
        ["file-text"] = Geometry.Parse("M 15 2 H 6 a 2 2 0 0 0 -2 2 v 16 a 2 2 0 0 0 2 2 h 12 a 2 2 0 0 0 2 -2 V 7 Z M 14 2 v 4 a 2 2 0 0 0 2 2 h 4 M 10 9 H 8 M 16 13 H 8 M 16 17 H 8"),
        ["file"] = Geometry.Parse("M 15 2 H 6 a 2 2 0 0 0 -2 2 v 16 a 2 2 0 0 0 2 2 h 12 a 2 2 0 0 0 2 -2 V 7 Z M 14 2 v 4 a 2 2 0 0 0 2 2 h 4"),
        ["folder"] = Geometry.Parse("M 20 20 a 2 2 0 0 0 2 -2 V 8 a 2 2 0 0 0 -2 -2 h -7.9 a 2 2 0 0 1 -1.69 -.9 L 9.6 3.9 A 2 2 0 0 0 7.93 3 H 4 a 2 2 0 0 0 -2 2 v 13 a 2 2 0 0 0 2 2 Z"),
        ["folder-open"] = Geometry.Parse("M 6 14 l 1.5 -2.9 A 2 2 0 0 1 9.24 10 H 20 a 2 2 0 0 1 1.94 2.5 l -1.54 6 a 2 2 0 0 1 -1.95 1.5 H 4 a 2 2 0 0 1 -2 -2 V 5 a 2 2 0 0 1 2 -2 h 3.9 a 2 2 0 0 1 1.69 .9 l .81 1.2 a 2 2 0 0 0 1.67 .9 H 18 a 2 2 0 0 1 2 2 v 2"),
        ["gauge"] = Geometry.Parse("M 12 14 l 4 -4 M 3.34 19 a 10 10 0 1 1 17.32 0"),
        ["git-branch"] = Geometry.Parse("M 6 3 L 6 15 M 15 6 a 3 3 0 1 0 6 0 a 3 3 0 1 0 -6 0 M 3 18 a 3 3 0 1 0 6 0 a 3 3 0 1 0 -6 0 M 18 9 a 9 9 0 0 1 -9 9"),
        ["git-commit-horizontal"] = Geometry.Parse("M 9 12 a 3 3 0 1 0 6 0 a 3 3 0 1 0 -6 0 M 3 12 L 9 12 M 15 12 L 21 12"),
        ["git-pull-request"] = Geometry.Parse("M 15 18 a 3 3 0 1 0 6 0 a 3 3 0 1 0 -6 0 M 3 6 a 3 3 0 1 0 6 0 a 3 3 0 1 0 -6 0 M 13 6 h 3 a 2 2 0 0 1 2 2 v 7 M 6 9 L 6 21"),
        ["globe"] = Geometry.Parse("M 2 12 a 10 10 0 1 0 20 0 a 10 10 0 1 0 -20 0 M 12 2 a 14.5 14.5 0 0 0 0 20 14.5 14.5 0 0 0 0 -20 M 2 12 h 20"),
        ["hammer"] = Geometry.Parse("M 15 12 l -8.373 8.373 a 1 1 0 1 1 -3 -3 L 12 9 M 18 15 l 4 -4 M 21.5 11.5 l -1.914 -1.914 A 2 2 0 0 1 19 8.172 V 7 l -2.26 -2.26 a 6 6 0 0 0 -4.202 -1.756 L 9 2.96 l .92 .82 A 6.18 6.18 0 0 1 12 8.4 V 10 l 2 2 h 1.172 a 2 2 0 0 1 1.414 .586 L 18.5 14.5"),
        ["image"] = Geometry.Parse("M 5 3 h 14 a 2 2 0 0 1 2 2 v 14 a 2 2 0 0 1 -2 2 h -14 a 2 2 0 0 1 -2 -2 v -14 a 2 2 0 0 1 2 -2 Z M 7 9 a 2 2 0 1 0 4 0 a 2 2 0 1 0 -4 0 M 21 15 l -3.086 -3.086 a 2 2 0 0 0 -2.828 0 L 6 21"),
        ["info"] = Geometry.Parse("M 2 12 a 10 10 0 1 0 20 0 a 10 10 0 1 0 -20 0 M 12 16 v -4 M 12 8 h .01"),
        ["list-checks"] = Geometry.Parse("M 3 17 l 2 2 4 -4 M 3 7 l 2 2 4 -4 M 13 6 h 8 M 13 12 h 8 M 13 18 h 8"),
        ["list-filter"] = Geometry.Parse("M 3 6 h 18 M 7 12 h 10 M 10 18 h 4"),
        ["lock-open"] = Geometry.Parse("M 5 11 h 14 a 2 2 0 0 1 2 2 v 7 a 2 2 0 0 1 -2 2 h -14 a 2 2 0 0 1 -2 -2 v -7 a 2 2 0 0 1 2 -2 Z M 7 11 V 7 a 5 5 0 0 1 9.9 -1"),
        ["lock"] = Geometry.Parse("M 5 11 h 14 a 2 2 0 0 1 2 2 v 7 a 2 2 0 0 1 -2 2 h -14 a 2 2 0 0 1 -2 -2 v -7 a 2 2 0 0 1 2 -2 Z M 7 11 V 7 a 5 5 0 0 1 10 0 v 4"),
        ["map"] = Geometry.Parse("M 14.106 5.553 a 2 2 0 0 0 1.788 0 l 3.659 -1.83 A 1 1 0 0 1 21 4.619 v 12.764 a 1 1 0 0 1 -.553 .894 l -4.553 2.277 a 2 2 0 0 1 -1.788 0 l -4.212 -2.106 a 2 2 0 0 0 -1.788 0 l -3.659 1.83 A 1 1 0 0 1 3 19.381 V 6.618 a 1 1 0 0 1 .553 -.894 l 4.553 -2.277 a 2 2 0 0 1 1.788 0 z M 15 5.764 v 15 M 9 3.236 v 15"),
        ["message-circle-question"] = Geometry.Parse("M 7.9 20 A 9 9 0 1 0 4 16.1 L 2 22 Z M 9.09 9 a 3 3 0 0 1 5.83 1 c 0 2 -3 3 -3 3 M 12 17 h .01"),
        ["paperclip"] = Geometry.Parse("M 21.44 11.05 l -9.19 9.19 a 6 6 0 0 1 -8.49 -8.49 l 8.57 -8.57 A 4 4 0 1 1 18 8.84 l -8.59 8.57 a 2 2 0 0 1 -2.83 -2.83 l 8.49 -8.48"),
        ["pencil"] = Geometry.Parse("M 21.174 6.812 a 1 1 0 0 0 -3.986 -3.987 L 3.842 16.174 a 2 2 0 0 0 -.5 .83 l -1.321 4.352 a .5 .5 0 0 0 .623 .622 l 4.353 -1.32 a 2 2 0 0 0 .83 -.497 z M 15 5 l 4 4"),
        ["plus"] = Geometry.Parse("M 5 12 h 14 M 12 5 v 14"),
        ["refresh-cw"] = Geometry.Parse("M 3 12 a 9 9 0 0 1 9 -9 9.75 9.75 0 0 1 6.74 2.74 L 21 8 M 21 3 v 5 h -5 M 21 12 a 9 9 0 0 1 -9 9 9.75 9.75 0 0 1 -6.74 -2.74 L 3 16 M 8 16 H 3 v 5"),
        ["star"] = Geometry.Parse("M 11.525 2.295 a .53 .53 0 0 1 .95 0 l 2.31 4.679 a 2.123 2.123 0 0 0 1.595 1.16 l 5.166 .756 a .53 .53 0 0 1 .294 .904 l -3.736 3.638 a 2.123 2.123 0 0 0 -.611 1.878 l .882 5.14 a .53 .53 0 0 1 -.771 .56 l -4.618 -2.428 a 2.122 2.122 0 0 0 -1.973 0 L 6.396 21.01 a .53 .53 0 0 1 -.77 -.56 l .881 -5.139 a 2.122 2.122 0 0 0 -.611 -1.879 L 2.16 9.795 a .53 .53 0 0 1 .294 -.906 l 5.165 -.755 a 2.122 2.122 0 0 0 1.597 -1.16 z"),
        ["search"] = Geometry.Parse("M 3 11 a 8 8 0 1 0 16 0 a 8 8 0 1 0 -16 0 M 21 21 l -4.3 -4.3"),
        ["shield-question"] = Geometry.Parse("M 20 13 c 0 5 -3.5 7.5 -7.66 8.95 a 1 1 0 0 1 -.67 -.01 C 7.5 20.5 4 18 4 13 V 6 a 1 1 0 0 1 1 -1 c 2 0 4.5 -1.2 6.24 -2.72 a 1.17 1.17 0 0 1 1.52 0 C 14.51 3.81 17 5 19 5 a 1 1 0 0 1 1 1 z M 9.1 9 a 3 3 0 0 1 5.82 1 c 0 2 -3 3 -3 3 M 12 17 h .01"),
        ["shield"] = Geometry.Parse("M 20 13 c 0 5 -3.5 7.5 -7.66 8.95 a 1 1 0 0 1 -.67 -.01 C 7.5 20.5 4 18 4 13 V 6 a 1 1 0 0 1 1 -1 c 2 0 4.5 -1.2 6.24 -2.72 a 1.17 1.17 0 0 1 1.52 0 C 14.51 3.81 17 5 19 5 a 1 1 0 0 1 1 1 z"),
        ["sparkles"] = Geometry.Parse("M 9.937 15.5 A 2 2 0 0 0 8.5 14.063 l -6.135 -1.582 a .5 .5 0 0 1 0 -.962 L 8.5 9.936 A 2 2 0 0 0 9.937 8.5 l 1.582 -6.135 a .5 .5 0 0 1 .963 0 L 14.063 8.5 A 2 2 0 0 0 15.5 9.937 l 6.135 1.581 a .5 .5 0 0 1 0 .964 L 15.5 14.063 a 2 2 0 0 0 -1.437 1.437 l -1.582 6.135 a .5 .5 0 0 1 -.963 0 z M 20 3 v 4 M 22 5 h -4 M 4 17 v 2 M 5 18 H 3"),
        ["square-pen"] = Geometry.Parse("M 12 3 H 5 a 2 2 0 0 0 -2 2 v 14 a 2 2 0 0 0 2 2 h 14 a 2 2 0 0 0 2 -2 v -7 M 18.375 2.625 a 1 1 0 0 1 3 3 l -9.013 9.014 a 2 2 0 0 1 -.853 .505 l -2.873 .84 a .5 .5 0 0 1 -.62 -.62 l .84 -2.873 a 2 2 0 0 1 .506 -.852 z"),
        ["terminal"] = Geometry.Parse("M 4 17 L 10 11 L 4 5 M 12 19 L 20 19"),
        ["ticket"] = Geometry.Parse("M 2 9 a 3 3 0 0 1 0 6 v 2 a 2 2 0 0 0 2 2 h 16 a 2 2 0 0 0 2 -2 v -2 a 3 3 0 0 1 0 -6 V 7 a 2 2 0 0 0 -2 -2 H 4 a 2 2 0 0 0 -2 2 Z M 13 5 v 2 M 13 17 v 2 M 13 11 v 2"),
        ["trending-down"] = Geometry.Parse("M 22 17 L 13.5 8.5 L 8.5 13.5 L 2 7 M 16 17 L 22 17 L 22 11"),
        ["trending-up"] = Geometry.Parse("M 22 7 L 13.5 15.5 L 8.5 10.5 L 2 17 M 16 7 L 22 7 L 22 13"),
        ["triangle-alert"] = Geometry.Parse("M 21.73 18 l -8 -14 a 2 2 0 0 0 -3.48 0 l -8 14 A 2 2 0 0 0 4 21 h 16 a 2 2 0 0 0 1.73 -3 M 12 9 v 4 M 12 17 h .01"),
        ["user-round"] = Geometry.Parse("M 7 8 a 5 5 0 1 0 10 0 a 5 5 0 1 0 -10 0 M 20 21 a 8 8 0 0 0 -16 0"),
        ["wrap-text"] = Geometry.Parse("M 3 6 L 21 6 M 3 12 h 15 a 3 3 0 1 1 0 6 h -4 M 16 16 L 14 18 L 16 20 M 3 18 L 10 18"),
        ["wrench"] = Geometry.Parse("M 14.7 6.3 a 1 1 0 0 0 0 1.4 l 1.6 1.6 a 1 1 0 0 0 1.4 0 l 3.77 -3.77 a 6 6 0 0 1 -7.94 7.94 l -6.91 6.91 a 2.12 2.12 0 0 1 -3 -3 l 6.91 -6.91 a 6 6 0 0 1 7.94 -7.94 l -3.76 3.76 z"),
        ["x"] = Geometry.Parse("M 18 6 6 18 M 6 6 l 12 12"),
    };

    private static readonly Geometry OpenAIMark = Geometry.Parse("F1 M 252.794 108.802 C 289.191 99.0484 326.265 110.305 351.148 135.135 C 385.113 126.072 422.85 134.862 449.492 161.505 C 476.136 188.149 484.925 225.888 475.862 259.85 V 259.854 C 500.696 284.735 511.95 321.81 502.198 358.207 C 492.447 394.602 464.161 421.084 430.215 430.217 C 421.083 464.162 394.603 492.448 358.206 502.199 C 321.812 511.951 284.734 500.693 259.852 475.864 C 225.887 484.927 188.15 476.137 161.507 449.495 C 134.864 422.851 126.073 385.111 135.136 351.149 C 110.304 326.266 99.0496 289.192 108.801 252.795 C 118.552 216.4 146.84 189.918 180.784 180.785 C 189.917 146.841 216.396 118.553 252.794 108.802 Z M 374.292 407.145 C 374.292 411.271 372.092 415.086 368.517 417.148 L 283.723 466.102 C 302.487 480.585 327.555 486.459 352.217 479.852 C 386.997 470.532 410.068 439.312 410.555 405.006 V 317.717 C 410.555 315.08 409.125 312.621 406.843 311.303 L 374.292 292.509 V 407.145 Z M 251.868 415.897 C 248.296 417.959 243.893 417.959 240.317 415.897 L 155.526 366.942 C 152.366 390.436 159.811 415.08 177.866 433.136 H 177.863 C 203.325 458.594 241.896 462.962 271.85 446.232 L 347.449 402.586 C 349.735 401.268 351.148 398.8 351.148 396.163 V 358.579 L 251.868 415.897 Z M 368.602 220.628 C 366.319 219.309 363.474 219.318 361.191 220.637 L 328.641 239.431 L 427.921 296.749 C 431.496 298.811 433.697 302.627 433.697 306.752 V 404.661 C 455.622 395.654 473.244 376.881 479.851 352.218 C 489.169 317.442 473.668 281.85 444.201 264.274 L 368.602 220.628 Z M 177.303 206.34 C 155.377 215.348 137.756 234.122 131.148 258.783 C 121.832 293.561 137.331 329.153 166.799 346.727 L 242.398 390.373 C 244.68 391.692 247.525 391.684 249.807 390.366 L 282.357 371.572 L 183.078 314.253 C 179.504 312.189 177.303 308.375 177.303 304.251 V 206.34 Z M 259.849 279.145 V 331.858 L 305.5 358.213 L 351.15 331.858 V 279.145 L 305.5 252.789 L 259.849 279.145 Z M 327.276 144.9 C 308.512 130.418 283.445 124.543 258.782 131.15 C 224.002 140.471 200.931 171.691 200.445 205.995 V 293.286 C 200.445 295.923 201.875 298.381 204.158 299.7 L 236.707 318.493 V 203.856 C 236.707 199.731 238.909 195.916 242.483 193.853 L 327.276 144.9 Z M 433.137 177.867 C 407.675 152.407 369.103 148.038 339.149 164.769 L 263.55 208.415 C 261.265 209.734 259.852 212.202 259.852 214.838 V 252.423 L 359.132 195.105 C 362.703 193.041 367.108 193.041 370.682 195.105 L 455.473 244.06 C 458.635 220.567 451.189 195.922 433.135 177.867 H 433.137 Z");
    private static readonly Rect OpenAIBox = new(100, 100, 411, 411);
    private static readonly Geometry ClaudeAIMark = Geometry.Parse("F1 M 50.228 170.321 l 50.357 -28.257 .843 -2.463 -.843 -1.361 h -2.462 l -8.426 -.518 -28.775 -.778 -24.952 -1.037 -24.175 -1.296 -6.092 -1.297 L 0 125.796 l .583 -3.759 5.12 -3.434 7.324 .648 16.202 1.101 24.304 1.685 17.629 1.037 26.118 2.722 h 4.148 l .583 -1.685 -1.426 -1.037 -1.101 -1.037 -25.147 -17.045 -27.22 -18.017 -14.258 -10.37 -7.713 -5.25 -3.888 -4.925 -1.685 -10.758 7 -7.713 9.397 .649 2.398 .648 9.527 7.323 20.35 15.75 L 94.817 91.9 l 3.889 3.24 1.555 -1.102 .195 -.777 -1.75 -2.917 -14.453 -26.118 -15.425 -26.572 -6.87 -11.018 -1.814 -6.61 c -.648 -2.723 -1.102 -4.991 -1.102 -7.778 l 7.972 -10.823 L 71.42 0 82.05 1.426 l 4.472 3.888 6.61 15.101 10.694 23.786 16.591 32.34 4.861 9.592 2.592 8.879 .973 2.722 h 1.685 v -1.556 l 1.36 -18.211 2.528 -22.36 2.463 -28.776 .843 -8.1 4.018 -9.722 7.971 -5.25 6.222 2.981 5.12 7.324 -.713 4.73 -3.046 19.768 -5.962 30.98 -3.889 20.739 h 2.268 l 2.593 -2.593 10.499 -13.934 17.628 -22.036 7.778 -8.749 9.073 -9.657 5.833 -4.601 h 11.018 l 8.1 12.055 -3.628 12.443 -11.342 14.388 -9.398 12.184 -13.48 18.147 -8.426 14.518 .778 1.166 2.01 -.194 30.46 -6.481 16.462 -2.982 19.637 -3.37 8.88 4.148 .971 4.213 -3.5 8.62 -20.998 5.184 -24.628 4.926 -36.682 8.685 -.454 .324 .519 .648 16.526 1.555 7.065 .389 h 17.304 l 32.21 2.398 8.426 5.574 5.055 6.805 -.843 5.184 -12.962 6.611 -17.498 -4.148 -40.83 -9.721 -14 -3.5 h -1.944 v 1.167 l 11.666 11.406 21.387 19.314 26.767 24.887 1.36 6.157 -3.434 4.86 -3.63 -.518 -23.526 -17.693 -9.073 -7.972 -20.545 -17.304 h -1.36 v 1.814 l 4.73 6.935 25.017 37.59 1.296 11.536 -1.814 3.76 -6.481 2.268 -7.13 -1.297 -14.647 -20.544 -15.1 -23.138 -12.185 -20.739 -1.49 .843 -7.194 77.448 -3.37 3.953 -7.778 2.981 -6.48 -4.925 -3.436 -7.972 3.435 -15.749 4.148 -20.544 3.37 -16.333 3.046 -20.285 1.815 -6.74 -.13 -.454 -1.49 .194 -15.295 20.999 -23.267 31.433 -18.406 19.702 -4.407 1.75 -7.648 -3.954 .713 -7.064 4.277 -6.286 25.47 -32.405 15.36 -20.092 9.917 -11.6 -.065 -1.686 h -.583 L 44.07 198.125 l -12.055 1.555 -5.185 -4.86 .648 -7.972 2.463 -2.593 20.35 -13.999 -.064 .065 Z");
    private static readonly Rect ClaudeAIBox = new(0, 0, 256, 257);

    // GitHub's mark, which T3 Code shows for pull requests on GitHub repos.
    private static readonly Geometry GitHubMark = Geometry.Parse("F1 M 8 0 C 3.58 0 0 3.58 0 8 c 0 3.54 2.29 6.53 5.47 7.59 .4 .07 .55 -.17 .55 -.38 0 -.19 -.01 -.82 -.01 -1.49 -2.01 .37 -2.53 -.49 -2.69 -.94 -.09 -.23 -.48 -.94 -.82 -1.13 -.28 -.15 -.68 -.52 -.01 -.53 .63 -.01 1.08 .58 1.23 .82 .72 1.21 1.87 .87 2.33 .66 .07 -.52 .28 -.87 .51 -1.07 -1.78 -.2 -3.64 -.89 -3.64 -3.95 0 -.87 .31 -1.59 .82 -2.15 -.08 -.2 -.36 -1.02 .08 -2.12 0 0 .67 -.21 2.2 .82 .64 -.18 1.32 -.27 2 -.27 .68 0 1.36 .09 2 .27 1.53 -1.04 2.2 -.82 2.2 -.82 .44 1.1 .16 1.92 .08 2.12 .51 .56 .82 1.27 .82 2.15 0 3.07 -1.87 3.75 -3.65 3.95 .29 .25 .54 .73 .54 1.48 0 1.07 -.01 1.93 -.01 2.2 0 .21 .15 .46 .55 .38 A 8.013 8.013 0 0 0 16 8 c 0 -4.42 -3.58 -8 -8 -8 Z");

    static AgentIcons()
    {
        GitHubMark.Freeze();
        foreach (var geometry in Lines.Values) geometry.Freeze();
        OpenAIMark.Freeze();
        ClaudeAIMark.Freeze();
    }

    /// <summary>A Lucide icon, stroked as T3 Code strokes them (1.75 wide, or 2 where it says stroke-2), with round ends.</summary>
    public static FrameworkElement Line(string name, double size, Brush brush, double stroke = 1.75, Brush? fill = null)
    {
        var path = new Path
        {
            Data = Lines.TryGetValue(name, out var geometry) ? geometry : Lines["circle"],
            Fill = fill,
            Stroke = brush,
            StrokeThickness = stroke,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
        };
        var canvas = new Canvas { Width = 24, Height = 24, Children = { path } };
        return new Viewbox { Width = size, Height = size, Child = canvas, SnapsToDevicePixels = true, IsHitTestVisible = false };
    }

    /// <summary>The Claude mark in its own colour, or OpenAI's in the brush given.</summary>
    public static FrameworkElement GitHub(double size, Brush brush)
    {
        var canvas = new Canvas { Width = 16, Height = 16, Children = { new Path { Data = GitHubMark, Fill = brush } } };
        return new Viewbox { Width = size, Height = size, Child = canvas, IsHitTestVisible = false };
    }

    public static FrameworkElement Provider(bool claude, double size, Brush? brush = null)
    {
        var box = claude ? ClaudeAIBox : OpenAIBox;
        var path = new Path
        {
            Data = claude ? ClaudeAIMark : OpenAIMark,
            Fill = claude ? new SolidColorBrush(Color.FromRgb(0xD9, 0x77, 0x57)) : brush ?? Brushes.White,
            RenderTransform = new TranslateTransform(-box.X, -box.Y),
        };
        var canvas = new Canvas { Width = box.Width, Height = box.Height, Children = { path }, ClipToBounds = true };
        return new Viewbox { Width = size, Height = size, Child = canvas, IsHitTestVisible = false };
    }
}
