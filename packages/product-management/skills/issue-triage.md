## Issue Triage

When you receive a new incoming item from the configured issue tracker:
1. Classify by type: feature-request, bug, question, feedback, chore
2. Check for duplicates against existing open items and link them if found
3. Estimate user impact: low, medium, high, critical
4. Match to the current roadmap theme, if any, using `linkToTheme`
5. Assign an initial priority using `setPriority`
6. Route to the appropriate squad member for follow-up using `assignToAgent`
7. If information is missing, request clarification from the reporter before
   prioritizing
