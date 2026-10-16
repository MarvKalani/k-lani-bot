"""Render the public README chart from the committed browser pilot measurements."""

import json
from pathlib import Path

import matplotlib

matplotlib.use("Agg")
import matplotlib.pyplot as plt


ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "benchmarks/compact-ui-20261007/measurements.json"
DESTINATION = ROOT / "assets/benchmarks"
ROUTES = ["computer-use", "bot-compact", "mcp-cartridge"]
LABELS = ["Browser Computer Use\nObject tree", "k-lani-bot\nCompact native UI", "Reusable test recipe\nTest-program MCP"]
COLORS = ["#82b8ff", "#65d6c3", "#b4e486"]
BACKGROUND = "#101b2b"
FOREGROUND = "#edf5ff"
MUTED = "#bdcce0"


def measured_groups():
    document = json.loads(SOURCE.read_text(encoding="utf-8"))
    if document["model"] != "gpt-6-luna" or document["reasoningEffort"] != "medium":
        raise ValueError("Review the model label before publishing a changed comparison.")
    groups = [next(group for group in document["groups"] if group["route"] == route) for route in ROUTES]
    for group in groups:
        if group["unmeasuredTrials"] or group["successes"] != 3 or group["trials"] != 3 or group["images"] != 0:
            raise ValueError("Review the README claims before publishing changed benchmark coverage.")
    return groups


def draw_bars(figure, groups):
    axes = figure.add_axes([0.30, 0.32, 0.64, 0.43])
    values = [group["totalTokensPerSuccess"] for group in groups]
    baseline = values[0]
    axes.set_facecolor(BACKGROUND)
    axes.barh([2, 1, 0], values, color=COLORS, height=0.48)
    axes.set_yticks([2, 1, 0], labels=LABELS, fontsize=12, color=FOREGROUND)
    axes.tick_params(axis="y", length=0, pad=18)
    axes.set_xlim(0, baseline * 1.18)
    axes.set_xticks([0, 100000, 200000, 300000], labels=["0", "100k", "200k", "300k"])
    axes.tick_params(axis="x", colors=MUTED, labelsize=10, length=0, pad=10)
    axes.set_axisbelow(True)
    axes.grid(axis="x", color="#304158", linewidth=0.7)
    for spine in axes.spines.values():
        spine.set_visible(False)
    for index, value in enumerate(values):
        axes.text(value + baseline * 0.018, 2 - index, f"{value:,.0f}", va="center", color=FOREGROUND, fontsize=12)
        if index:
            reduction = 100 * (1 - value / baseline)
            axes.text(value * 0.5, 2 - index, f"−{reduction:.1f}%", va="center", ha="center",
                      color=BACKGROUND, fontsize=12, weight="bold")


def render():
    groups = measured_groups()
    plt.rcParams["font.family"] = "DejaVu Sans"
    plt.rcParams["svg.hashsalt"] = "k-lani-bot-readme-benchmark"
    figure = plt.figure(figsize=[12, 6.3], facecolor=BACKGROUND)
    figure.text(0.045, 0.90, "Model tokens per completed task", color=FOREGROUND, fontsize=23, weight="bold")
    figure.text(0.045, 0.83, "Browser pilot · GPT-6 Luna medium · 3 matched datasets · 3/3 passed per route",
                color=MUTED, fontsize=11)
    draw_bars(figure, groups)
    figure.text(0.045, 0.21, "Same task: add five amounts → write HTML → save → reopen and verify", color=FOREGROUND, fontsize=11)
    figure.text(0.045, 0.145, "Input + output, including cached context. All attempts counted. Zero model images in these routes.",
                color=MUTED, fontsize=10)
    figure.text(0.045, 0.10, "77%: test-program recipe. Full-workflow savings from auto-captured UI functions remain unmeasured.",
                color=MUTED, fontsize=10)
    figure.text(0.045, 0.045, "Source: benchmarks/compact-ui-20261007/measurements.json   ·   k-lani-bot", color=MUTED, fontsize=9)
    DESTINATION.mkdir(parents=True, exist_ok=True)
    figure.savefig(DESTINATION / "readme-comparison.png", dpi=180, facecolor=BACKGROUND)
    figure.savefig(DESTINATION / "readme-comparison.svg", facecolor=BACKGROUND, metadata={"Date": None})
    plt.close(figure)


if __name__ == "__main__":
    render()
