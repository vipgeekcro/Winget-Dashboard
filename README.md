# Winget Dashboard

**Winget Dashboard** is a free and open-source Windows application that provides an accessible graphical interface for managing software with WinGet.

The project is designed with keyboard and screen reader accessibility as a core requirement, with particular attention to **NVDA**. Its goal is to make common WinGet tasks easier to perform without requiring users to work directly from the command line.

## Features

Winget Dashboard brings several WinGet management tasks together in one application:

- **Search** — search the WinGet catalog and install applications.
- **Updates** — find available application updates and update individual or multiple applications.
- **Installed Applications** — view installed applications and uninstall selected software.
- **Lists** — create and manage reusable lists of applications.
- **Catalog** — build and update the local package catalog used by the application.
- **Settings** — configure application preferences, including language.

The application uses a local catalog for fast package searching and provides dedicated interfaces for installation, updating and application management.

## Accessibility

Accessibility is one of the main goals of Winget Dashboard.

The interface is designed for full keyboard operation and screen reader use, with particular attention to NVDA. Development focuses on predictable keyboard focus, accessible control names and states, logical navigation, standard keyboard interaction and clear status information during longer operations.

The application also follows Windows system colors and supports Windows Contrast and High Contrast modes.

Feedback from users of NVDA and other screen readers is welcome.

## Languages

Winget Dashboard includes support for:

- English
- Croatian
- Slovenian
- German
- French
- Spanish
- Italian

English is the built-in fallback language.

## Download

Ready-to-use versions of Winget Dashboard are available from the **Releases** section of this repository.

Download the latest installer and run `Winget-Dashboard-X.X.X-Setup.exe` to install the application.

## Requirements

Winget Dashboard requires:

- Windows 10 or Windows 11
- WinGet
- 64-bit Windows

Some software installation, update and removal operations may require administrator privileges. Winget Dashboard requests elevation when required rather than running the entire application as administrator.

## Source Code

The complete source code is available in this repository.

Winget Dashboard is developed in **C#**, using **WPF** and **.NET 10**.

The application communicates with Windows Package Manager and uses WinGet functionality for package discovery and management.

## Development

Winget Dashboard is created with the assistance of AI tools, including **ChatGPT and Codex**.

The author is not a professional programmer. Development is based on his ideas, practical experience as a blind Windows user, accessibility requirements and extensive real-world testing. The author defines the functionality and user experience and personally tests the application with NVDA, while AI tools assist with development and writing the code.

## Feedback and Issues

If you find a bug, encounter an accessibility problem or have a suggestion for improvement, please open an **Issue** in this GitHub repository.

Contributions, testing and accessibility feedback are welcome.
