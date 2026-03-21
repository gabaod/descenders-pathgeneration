# descenders-pathgeneration
path generation script which allows curbs, berms, materials, painted path, slope steepness etc.<br><br><br>
Place RoadPathGenerator.cs into Assets/<br>
Place RoadPathGeneratorEditor.cs into Assets/Editor<br>
goto tools - road path generator - create road generator<br>  this will create a new object in hierarchy with the script assigned and goto inspector tab to define settings<br>
tutorial here - https://youtu.be/adLsvkC-MAI <br><br><br><br>
Gizmo color key <br>
        // ── Colour key ────────────────────────────────────────
        // Cyan        — centreline
        // Red         — road edges
        // Orange      — flatten extra width
        // Yellow      — flatten falloff outer / flatten beyond path outer
        // Green       — berm slope outer
        // Light green — berm outer falloff toe
        // Blue        — ditch inner wall top
        // Cornflower  — ditch floor start
        // Purple      — ditch floor end
        // Magenta     — ditch outer wall toe
        // White       — shoulder smooth outer
        // Pink        — ridge cap zone inner / outer
        // Teal        — curb outer edge
