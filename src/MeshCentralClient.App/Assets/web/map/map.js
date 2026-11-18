// The devices on a map, as the website's: OpenStreetMap tiles, a dot per device (green online, grey offline), its name,
// where its place comes from and a button that opens it in the app. The host calls setDevices(json); a tap on "Open"
// goes back to the host (WebView2 postMessage on Windows, the "app" web message listener on Android).
// Los equipos en un mapa, como en la web: un punto por equipo, su nombre, de dónde sale su sitio y un botón que lo abre.
"use strict";
var map = L.map("map", { worldCopyJump: true }).setView([40.4, -3.7], 3);
L.tileLayer("https://tile.openstreetmap.org/{z}/{x}/{y}.png", {
  maxZoom: 19,
  attribution: '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a>'
}).addTo(map);
var layer = L.layerGroup().addTo(map), fitted = false;

function send(id) {
  if (window.chrome && window.chrome.webview) window.chrome.webview.postMessage(id);
  else if (window.app) window.app.postMessage(id);
}

function text(s) { var d = document.createElement("div"); d.textContent = s; return d.innerHTML; }

// data: { devices: [{ id, name, lat, lon, online, kind }], labels: { online, offline, open, none, kinds: { 1..4 } } }
function setDevices(data) {
  layer.clearLayers();
  var bounds = [];
  data.devices.forEach(function (d) {
    var m = L.circleMarker([d.lat, d.lon], { radius: 9, weight: 2, color: "#fff", fillOpacity: 0.95,
      fillColor: d.online ? "#2e7d32" : "#757575" }).addTo(layer);
    var box = document.createElement("div");
    box.innerHTML = '<div class="name">' + text(d.name) + '</div><div class="facts">' +
      text((d.online ? data.labels.online : data.labels.offline) + " · " + (data.labels.kinds[d.kind] || "")) +
      '</div><button class="open">' + text(data.labels.open) + '</button>';
    box.querySelector("button").addEventListener("click", function () { send(d.id); });
    m.bindPopup(box);
    m.bindTooltip(d.name);
    bounds.push([d.lat, d.lon]);
  });
  var empty = document.getElementById("empty");
  empty.textContent = data.labels.none;
  empty.style.display = bounds.length ? "none" : "block";
  // The first time, everything in sight; later the person's zoom is kept. / La primera vez, todo a la vista.
  if (bounds.length && !fitted) { map.fitBounds(bounds, { padding: [40, 40], maxZoom: 13 }); fitted = true; }
}
