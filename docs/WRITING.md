# Writing guide (everything a person reads in the app)

The owner wants the app to read as a professional, trustworthy product: formal, polite and precise, while still
easy for people who are not technical.

## Voice

- Formal and courteous. No contractions ("do not", "cannot", "it is"), no slang, no exclamation marks, no jokes.
- The app does not speak as "I". Name the action or the app: "AutoFix Scout will restart the Windows Audio
  service." / "The camera is turned off in Windows." Address the person as "you".
- Complete sentences, sentence case, one idea per sentence, at most about 25 words.
- Plain words first; a technical term only when the person will see it in Windows (Device Manager, driver,
  Airplane mode), with the exact on-screen name in quotation marks.
- State what will happen, whether it can be undone, and when Windows will ask for permission.
- Never blame the person. Never promise a result ("This should fix it" -> "This often resolves the problem.").

## Patterns

| Situation | Write |
|---|---|
| Finding | "The sound output is muted." |
| Offer a fix | "AutoFix Scout can turn the volume back on. You can change it again at any time." |
| Needs admin | "... Windows will ask for your permission." |
| Driver change | "AutoFix Scout will create a save point first and will restore it automatically if anything goes wrong." |
| Person must act | "Please open ... and select ..." |
| Test question | "Did you hear the test sound?" |
| Fixed | "The problem has been resolved. Your sound is working again." |
| No fault | "No problem was found, so no changes were made. If the problem returns, please run the check again while it is happening." |
| Not fixed | "AutoFix Scout could not resolve this problem. A report has been prepared for the person helping you." |

## Buttons and labels

Short verbs, title case not needed: "Continue", "Apply fix", "Skip", "Yes, it works", "No, it does not", "Close",
"Restore driver", "Not now". The home choices stay symptom-first: "Sound is not working", "Camera is not working",
"Cannot connect to the internet", "Something else".

## Keep

- Meaning, order of steps, and every safety statement (permission prompts, save points, automatic undo).
- Names the person must find in Windows, exactly as Windows shows them.
- Helper reports may stay technical; their headings follow this guide.
