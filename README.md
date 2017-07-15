# SecurityAnalyzer
Tool to analyze a solution's MVC controllers and the code they call (direct and via interface) for references to a enum.  I intend to use this to analyze a solution to make sure the security component enum used for various checks are consistent throughout the layers.

Uses Roslyn for code analysis and ASP.Net Core (Kestrel) to serve the result to a local browser.

To use, run `SecurityAnalyzer.exe <fullpathtosolution.sln>`.  A console app will appear, analyze the target solution, then launch your default web browser pointing to a simple website to display the results.  When complete, close the console app.
