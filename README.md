# Coal Mine AR/VR Training App

VR/AR-Based Safety Training and Skill Assessment System for High-Risk Coal Mining Operations.

A single Android application containing two modes:
- **AR Mode** — recognizes real-world mining equipment (via a printed/photographed image) and overlays safety information, warnings, and inspection steps.
- **VR Mode** — a phone-based VR walkthrough of a simulated mine environment where trainees practice responding to high-risk hazard scenarios (e.g. gas leaks, fire, equipment failure).

---

## Project Status

🚧 **In development.** Currently working AR foundation: camera pass-through, plane detection, tap-to-place, and image recognition (Image Target) are functional. VR Mode, scoring system, backend, and dashboard are not yet started.

---

## Tech Stack

| Layer | Technology |
|---|---|
| App (AR + VR) | Unity 6 (6000.5.10f1), C# |
| AR | AR Foundation, ARCore XR Plugin |
| VR | XR Interaction Toolkit, OpenXR |
| Backend (planned) | Node.js, Express, MongoDB Atlas |
| Dashboard (planned) | React + Vite |
| Version Control | Git, Git LFS (for large binary assets) |

---

## Project Structure

```
CoalMineVR/
├── Assets/
│   ├── ARImages/       # Reference images used for AR Image Target recognition
│   ├── Prefabs/        # Reusable objects (e.g. placement Cube)
│   ├── Scenes/         # Unity scenes
│   ├── Scripts/        # C# scripts (AR/VR logic)
│   └── Settings/       # URP render pipeline & project settings assets
├── Packages/           # Unity package dependencies
├── ProjectSettings/    # Unity project configuration
├── .gitignore          # Unity-specific ignore rules
└── .gitattributes      # Git LFS tracking rules for large files
```

---

## Setup Guide (For Team Members)

### 1. Install required tools
- [Git](https://git-scm.com)
- [Git LFS](https://git-lfs.com)
- [VS Code](https://code.visualstudio.com) with the **C# Dev Kit** extension
- [Unity Hub](https://unity.com/download)
- Unity Editor **6000.5.10f1** (install via Unity Hub), with the **Android Build Support** module (including SDK, NDK, OpenJDK)

### 2. Set up Git LFS (one-time, per machine)
```bash
git lfs install
```

### 3. Clone the repository
```bash
git clone https://github.com/bramhagulavani/coal-mine-training.git
```

### 4. Open the project in Unity
1. Open **Unity Hub → Projects → Add**
2. Select the cloned `CoalMineVR` folder
3. Open it — first-time asset import may take several minutes

### 5. Point Unity to VS Code
`Edit → Preferences → External Tools → External Script Editor → Visual Studio Code`

### 6. Verify setup
- `File → Build Settings` should show **Android** as the active platform
- `Window → Package Manager` should list **AR Foundation**, **ARCore XR Plugin**, **XR Interaction Toolkit**, **OpenXR Plugin**

---

## Daily Git Workflow

```bash
# Before starting work
git pull

# After finishing work
git add .
git commit -m "clear description of what changed"
git push
```

**Avoid** multiple people editing the same scene file at the same time — this causes merge conflicts that are hard to resolve in Unity scene files.

---

## Current Features Implemented

- [x] AR camera pass-through (fixed via AR Background Renderer Feature on URP Renderer)
- [x] AR plane detection + tap-to-place object
- [x] AR Image Target recognition (tested with a placeholder image; real equipment photos pending)
- [ ] AR equipment info overlay UI (name, warning, inspection steps)
- [ ] VR mine environment
- [ ] VR hazard scenario with gaze-based interaction
- [ ] Scoring and feedback system
- [ ] Backend (Node.js + MongoDB)
- [ ] Trainer dashboard (React)

---

## Team

| Name | Roll No. |
|---|---|
| Krushnansh Sanjay Meher | 1252030009 |
| Harshvardhan Shivaji Dhere | 1252030014 |
| Atharv Dilip Rahate | 1252030026 |
| Bramha Vinayak Gulavani | 12520068 |

**Guide:** Prof. Kalyani Ghuge — Department of CSE (AIML), Vishwakarma Institute of Technology