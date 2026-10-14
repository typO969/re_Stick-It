# re_Stick-It

Lightweight desktop sticky notes for Windows, built with WPF on `.NET 8`, blah, blah, blah.

## So one day I was sitting in class, using an Apple and I noticed the version of 'Post-It" notes they get to use n things.
## And then I compared the version that Windows has and it made me a sad panda. It isn't fair that Swift programs look way more polished and dont make me feel embarrassed to use them in public!!

### I decided to take their good app design as a challenge and build a note simulator of my own!!

### As I sat around day-dreaming about this program and actively ignoring and procrastinating in all my classes,
-<u>I decided this version would be as real as possible in features. pretty mostly.</u>-<br>
I thought about all the raisins to do this and the raisins not to do it and then did it anyway.

### THIS meant: 
- no resizing
- only the colors my eyes could find at the store
- you can stick your notes to any surface and they will stay there for the most part
- no videos or music! how tf would you do that in real life!?!? get real, you monster!!
- i also added support for windows ink, so now you can draw on your notes! unfortunately for me i cant read my handwriting.. :(
  - so every one wins, but not me...
- plus other more extra bonus hidden improved items not found in stores!

### So, before you read the next section -OR- use the app, just keep in mind I was aiming for realism (while aiming with hand grenades).

## My version is -4x times better than Notezilla.

## For some reason, there was a couple days I couldnt keep my internal narrator internal.

## So I excitedly thought/yelled at some random person who just wanted to pay for their Dairy Queen and leave, <br>
   "I can do what you do!!!" I yelled with no context as to what and why i was yelling. "You better watch out! I can do it better and cheaper!"<br>
   I was also pretty drunk, which helped explain why I couldnt find any pants to put on and wear.

## That innocent standbyer was completely spaced out and I startled the rando so profusely that they ran at me, punched me, giggled, and then ran out of DQ.<br>
  I hope you've enjoyed this ancient tale of wonder and excitement!!! It has absolutely nothing to do with post-it notes.
  and none of it will be on any quizzes or finals! so stop looking so worried and scared!!
#
#

## Features

- Multiple notes with independent color, position, and content
- Rich text persistence (RTF + plain text fallback)
- Auto-save with debounce (typing/move safe)
- System tray integration:
  - New note
  - Minimize/restore/show all notes
  - Save all notes
  - Exit app
- Single-instance app behavior
- Restore notes on startup (with monitor-aware and virtual desktop aware placement)
- Optional “keep notes inside desktop area”
- Preferences for theme, taskbar/tray visibility, startup behavior, and default font
- Finally fixed sticking behavior. Notes should now stick to any window with 1 click!
- Notes gradually age and get bent out of shape over time.
- Notes can have slightly off angles, just like real life
- Just like in real life, these notes arent very practical if you have lots to say or remember
- Extensive help file coming soon.

## Tech Stack

- `C# 12`
- `.NET 8`
- `WPF` (Windows Desktop)

## Getting Started

### Prerequisites

- Windows 10/11
- `.NET 8 SDK`
- Visual Studio 2022/2026 with WPF workload

### Build & Run

1. Clone the repo
2. Open the solution in Visual Studio
3. Set `re_Stick-It` as startup project
4. Build and run (`F5`)

## Project Structure (high level)

- `re_Stick-It/App.xaml.cs` — app lifecycle, window spawning, save queue, tray behavior
- `re_Stick-It/Notes/` — note windows and note management UI
- `re_Stick-It/Persistence/` — JSON storage + persisted models
- `re_Stick-It/Services/` — tray, theme, startup registry, monitor affinity, etc.
- `re_Stick-It/Sticky/` — sticky target support/services

## Persistence

Application state is persisted as JSON and includes:

- Notes (geometry, content, title, color, font, sticky metadata)
- App preferences

## ~Screenshots~Photo Gallery

> Enjoy these real photos I took of post-it notes!!
<img width="500" alt="post2" src="https://github.com/user-attachments/assets/12f0eb1e-1763-475b-b0eb-9d08657bee1f" />
<img width="500" alt="post04" src="https://github.com/user-attachments/assets/e6e07a06-56db-4659-9259-f69fc0a11cde" />
<img width="500" alt="post01" src="https://github.com/user-attachments/assets/251c856b-da55-4c99-a538-7b3f6824320c" />


## Roadmap

- [ ] ~Search/filter notes~  haha, no, nice try! probably not, copilot.
- [X] Import/export notes
- [x] Optional cloud sync - it works if you use sync.com or just LAN filesharing.
- [x] Keyboard shortcut customization  - also mostly done.

## Contributing Conclusions

- Feel free to fix any mistakes I've made (including this haircut! You're just lucky you don't have to see it).
- Um, I wasn't originally going to make this available to the public for all the raisins, but i did, so don't make regret it by making me fix a bunch of code...

## Epilogue

- Ok, now get off your computer or phone and go outside. 

- Go and ask her/him out.


