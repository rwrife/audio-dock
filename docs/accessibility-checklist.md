# Accessibility implementation checklist

This checklist records repository evidence for issue #4. It is not a physical-device, assistive-technology, or screen-reader validation claim.

| Requirement | Implemented evidence | Validation still required on Windows |
|---|---|---|
| Keyboard reachability | Native WPF controls, access-key underscores, logical XAML order, and commands for create/capture/rename/duplicate/delete/preview/apply/cancel/undo/settings. | Keyboard-only walkthrough on Windows. |
| Programmatic names | `AutomationProperties.Name`/`HelpText` identify lists, editors, plan, activity, status, cancellation, and hotkey fields; visible text labels the remaining native controls. | Inspect UI Automation tree. |
| Logical and visible focus | Controls follow visual document order; initial focus moves to the scene list; native WPF focus visuals are not overridden. | Verify every focus transition and visible cue. |
| 200% scaling | Scroll viewers, grid star sizing, minimum window size, wrapping text, and no pixel-sized text. | Exercise Windows 200% display scaling and text scaling. |
| High contrast | UI uses native controls and dynamic system brushes for the status border; no fixed foreground/background palette is introduced. | Exercise Windows high-contrast themes. |
| Non-color-only cues | Match status says Matched/Missing/Ambiguous; plan has textual dispositions and explanations; activity has state/detail/observed columns; hotkeys report registered/conflict/disabled in text. | Confirm cues remain understandable across themes. |
| Status persistence | Apply and undo records are bounded and written atomically to local JSON; complete per-operation failure/rollback facts remain in Activity. | Verify restart behavior on Windows. |

No screen-reader result is claimed. Screen-reader testing is intentionally left unchecked until performed and documented with the actual product build and Windows environment.
