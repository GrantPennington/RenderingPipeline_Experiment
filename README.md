# Rendering Reconstruction Sandbox

## Overview

Rendering Reconstruction Sandbox is a Unity 6.x URP project for experimenting with low-resolution rendering and image presentation. The current prototype renders a scene either at native resolution or into a smaller render target, then presents that result back to the screen with different upscale methods.

At a high level, the project demonstrates a simplified version of the same pipeline ideas behind modern reconstruction techniques: render less, present more intelligently, and study the visual and performance tradeoffs along the way.

## Motivation

I started this project because I wanted a hands-on way to learn how modern rendering pipelines actually work.

Techniques like DLSS, FSR, and XeSS are easy to talk about in abstract terms, but they make a lot more sense when you build the simpler pieces yourself first. Instead of only reading articles or watching breakdowns, this sandbox is a way to test ideas directly:

- render at fewer pixels
- inspect how presentation changes the final image
- compare simple upscale methods
- build toward more advanced reconstruction step by step

## Features (Current)

- Toggle between:
  - Native resolution
  - Half resolution
  - Quarter resolution
- Compare upscale methods:
  - Nearest Neighbor: pixelated and blocky
  - Bilinear: smoother, but blurrier
  - Sharpened Bilinear: bilinear plus a simple sharpen step to recover perceived detail
- Switch modes in real time while the scene is running
- Debug overlay showing:
  - current render scale
  - current upscale method
  - render resolution
  - screen resolution
- Shader-based fullscreen presentation path for low-resolution output

## What I'm Learning

- How a rendering pipeline flows from camera -> render target -> presentation
- Why pixel count has such a large impact on GPU cost
- How sampling and filtering change the final image during upscaling
- The tradeoffs between sharpness, blur, stability, and artifacts
- Where a more advanced reconstruction stage, including AI-based methods, would fit into the pipeline

## Future Plans

- Temporal accumulation using previous frames
- Motion-aware reprojection
- Better reconstruction and sharpening filters
- Exploring lightweight ML-based upscaling ideas
- Possible webcam / AR stylization experiments built on the same rendering concepts

## Tech Stack

- Unity 6.x
- URP (Universal Render Pipeline)
- C#
- HLSL shaders

## How to Run

1. Open the project in Unity 6.x.
2. Open `Assets/Scenes/SampleScene.unity`.
3. Press Play.
4. Use the controls below to switch modes at runtime.

```text
1 / 2 / 3  -> Change render scale
Q / W / E  -> Change upscale method
```

## Notes

- This is an experimental sandbox, not production code.
- The focus is learning, iteration, and understanding the rendering path clearly.
- Expect rough edges as new slices are added and older experiments get replaced.
