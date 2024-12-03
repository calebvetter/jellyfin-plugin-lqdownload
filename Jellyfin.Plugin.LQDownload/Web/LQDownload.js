let currentUrl = "";
let pageLoadTime = 0;
let configResolution = "";
let needsTranscoding = false;
let transcodeProgress = 0;

// Initial load
onPageChange();

// Save the original methods
const originalPushState = history.pushState;
const originalReplaceState = history.replaceState;

// Override `pushState`
history.pushState = function (...args) {
	const result = originalPushState.apply(this, args);
	onPageChange();
	return result;
};

// Override `replaceState`
history.replaceState = function (...args) {
	const result = originalReplaceState.apply(this, args);
	onPageChange();
	return result;
};

// Listen for browser navigation
window.addEventListener("popstate", onPageChange);

// Listen for app command events
document.addEventListener("command", (e) => {
	if (e.target.classList.contains("btnMoreCommands")) onMoreButtonClick();
});

function getItemIdFromUrl() {
	const pseudoUrl = new URL(new URL(currentUrl).hash.replace("#", location.origin));
	return pseudoUrl.searchParams.get("id");
}

/**
 * When page changes
 */
function onPageChange() {
	if (location.href == currentUrl) return;
	currentUrl = location.href;
	pageLoadTime = Date.now();

	console.log("LQDOWNLOAD: page change", currentUrl);

	// Reset values
	needsTranscoding = false;
	transcodeProgress = 0;

	// Stop if not on details page
	if (!currentUrl.includes("/#/details")) return;

	// Get item ID from URL
	const itemId = getItemIdFromUrl();
	if (itemId) getItemTranscodeStatus(itemId);
}

/**
 * Load values for item
 */
async function getItemTranscodeStatus(itemId) {
	console.log("LQDOWNLOAD: getItemTranscodeStatus for itemId", itemId);

	const urlBase = location.href.split("web/#")[0];
	const url = `${urlBase}LQDownload/ClientScript/TranscodeStatus?itemId=${encodeURIComponent(itemId)}`;

	try {
		const response = await fetch(url, {
			method: "GET",
			headers: {
				"Content-Type": "application/json",
			},
		});

		if (!response.ok) {
			console.error("LQDOWNLOAD: Failed to fetch transcode status. HTTP status:", response.status);
			return;
		}

		// Check that we're still on this item's page
		const urlItemId = getItemIdFromUrl();
		if (urlItemId != itemId) return;

		const result = await response.json();
		console.log("LQDOWNLOAD: Transcode status result:", result);

		configResolution = result.configResolution;
		needsTranscoding = result.needsTranscoding;
		transcodeProgress = result.transcodeProgress;

		updateProgressUI(itemId);
	} catch (error) {
		console.error("LQDOWNLOAD: Error fetching transcode status:", error);
	}
}

/**
 * Update the UI with progress or complete
 */
function updateProgressUI(itemId) {
	if (!needsTranscoding) {
		let progressEls = document.querySelectorAll(".lqdownload-transcoding-progress");
		console.log("LQDOWNLOAD: progressEls", progressEls);

		if (progressEls.length > 0 && (transcodeProgress == 0 || transcodeProgress == 100)) {
			console.log("LQDOWNLOAD: transcode complete");
			transcodeProgress = 100;
		}

		if (transcodeProgress > 0) {
			if (progressEls.length == 0) {
				console.log("LQDOWNLOAD: add progress section");

				// Recursive function to check for versionContainers
				function waitForVersionContainers() {
					const versionContainers = document.querySelectorAll("form.trackSelections .selectSourceContainer");

					if (versionContainers.length > 0) {
						console.log("LQDOWNLOAD: versionContainers found, adding progress section");
						// Add progress section
						const progressEl = createTranscodeProgressElement();
						versionContainers.forEach((versionContainer) => {
							versionContainer.insertAdjacentElement("afterend", progressEl.cloneNode(true));
						});
						updateProgressUI(itemId); // Retry the main function
					} else if (Date.now() - pageLoadTime < 10000) {
						// Retry after 100ms if within 10 seconds of page load
						console.log("LQDOWNLOAD: waiting for versionContainers...");
						setTimeout(waitForVersionContainers, 100);
					} else {
						console.warn("LQDOWNLOAD: versionContainers not found within 10 seconds.");
					}
				}

				waitForVersionContainers(); // Start the recursive check
				return; // Stop further execution until containers are loaded
			}

			if (progressEls.length > 0) {
				console.log("LQDOWNLOAD: set progress");
				// Set progress
				progressEls.forEach((el) => {
					el.querySelector(".lqdownload-progress-bar").style.width = `${transcodeProgress}%`;
					el.querySelector(".lqdownload-progress-percent").innerHTML = `${Math.round(transcodeProgress)}%`;
				});

				if (transcodeProgress < 100) {
					setTimeout(() => getItemTranscodeStatus(itemId), 2000);
				} else {
					updateUIComplete();
				}
			}
		}
	}
}

/**
 * Update the UI to show completed and refresh button
 */
function updateUIComplete() {
	console.log("LQDOWNLOAD: show completed");
	const progressEls = document.querySelectorAll(".lqdownload-transcoding-progress");
	progressEls.forEach((el) => {
		// Remove percent
		el.querySelector(".lqdownload-progress-percent").remove();

		// Create button and replace progress bar
		const button = document.createElement("button");
		button.type = "button";
		button.style.appearance = "none";
		button.style.border = "none";
		button.style.display = "block";
		button.style.width = "100%";
		button.style.height = "100%";
		button.style.backgroundColor = "#007ea8";
		button.style.color = "#fff";
		button.style.cursor = "pointer";
		button.textContent = "Refresh Page";
		el.querySelector(".lqdownload-progress-bar").replaceWith(button);

		// Handle button clicks (refresh page)
		button.addEventListener("click", () => location.reload());
	});
}

/**
 * When the More button is clicked on movie/show details page
 */
function onMoreButtonClick() {
	const timeout = 1000; // 1 second timeout
	const startTime = performance.now();

	function waitForDownloadButton() {
		const dialogs = document.querySelectorAll(".dialogContainer .dialog.opened");
		const dialog = dialogs.length ? dialogs[dialogs.length - 1] : null;

		if (dialog) {
			const downloadButton = dialog.querySelector('button[data-id="download"]');
			if (downloadButton) {
				// This is a downloadable item.
				// Check for pre - transcoded file and display new download button
				addTranscodeButton(dialog, downloadButton);
				return;
			}
		}

		// Retry on the next frame
		if (performance.now() - startTime < timeout) requestAnimationFrame(waitForDownloadButton);
	}

	requestAnimationFrame(waitForDownloadButton);
}

/**
 * Adds transcode button to the menu
 */
async function addTranscodeButton(dialog, downloadButton) {
	if (!needsTranscoding) return;

	// Right-align popup
	dialog.style.left = "unset";
	dialog.style.right = "10px";

	// Create transcode button
	const newButton = document.createElement("button");
	newButton.setAttribute("is", "emby-button");
	newButton.setAttribute("type", "button");
	newButton.className = "listItem listItem-button actionSheetMenuItem emby-button";
	newButton.setAttribute("data-id", "download-lq");

	// Create inner content for the button
	newButton.innerHTML = `
		<span class="actionsheetMenuItemIcon listItemIcon listItemIcon-transparent material-icons video_settings" aria-hidden="true"></span>
		<div class="listItemBody actionsheetListItemBody">
				<div class="listItemBodyText actionSheetItemText">Create ${configResolution} Version</div>
		</div>
	`;

	// Add the button click event listener
	newButton.addEventListener("click", onTranscode);

	// Insert the new button after the downloadButton in the DOM
	downloadButton.insertAdjacentElement("afterend", newButton);
}

/**
 * Transcode button click
 */
async function onTranscode() {
	// Get item ID from URL
	const itemId = getItemIdFromUrl();

	console.log("LQDOWNLOAD: transcode itemId", itemId);

	if (!itemId) return;

	needsTranscoding = false;

	const urlBase = location.href.split("web/#")[0];
	const url = `${urlBase}LQDownload/ClientScript/Transcode?itemId=${encodeURIComponent(itemId)}`;

	try {
		const response = await fetch(url, {
			method: "GET",
			headers: {
				"Content-Type": "application/json",
			},
		});

		if (!response.ok) {
			console.error("LQDOWNLOAD: Failed to start transcode. HTTP status:", response.status);
			return;
		}

		const result = await response.json();
		console.log("LQDOWNLOAD: Transcode start result:", result);

		transcodeProgress = 0.01;

		updateProgressUI(itemId);
	} catch (error) {
		console.error("LQDOWNLOAD: Error fetching transcode status:", error);
	}
}

/**
 * Creates an html element for transcode progress
 */
function createTranscodeProgressElement() {
	const tempContainer = document.createElement("div");
	tempContainer.innerHTML = `<div class="lqdownload-transcoding-progress" style="margin: 0 0 0.3em; display: flex; max-width: 44em">
			<div style="line-height: 1.75; margin: 0 0.2em 0 0; flex-basis: 6.25em; flex-grow: 0; flex-shrink: 0">Transcoding</div>
			<div style="position: relative; width: 100%; border-radius: 0.2em; overflow: hidden; background: #292929">
				<div class="lqdownload-progress-bar" style="width: 0; height: 100%; background: #007ea8; transition: width 2s"></div>
				<div class="lqdownload-progress-percent" style="position: absolute; top: 0; left: 0; right: 0; bottom: 0; display: flex; justify-content: center; align-items: center">0%</div>
			</div>
		</div>`;
	return tempContainer.firstElementChild;
}
