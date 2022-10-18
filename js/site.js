
let LARGE_SIZE = 1200;
var origSizes = {};

var images = document.querySelectorAll('img');

for (let image of images) {
    image.addEventListener('click', function (mouseEvent) {
        // this is the htmlimageelement being clicked
        onClicked(this);
    });
}

function onClicked(image) {

    if (image.width < LARGE_SIZE) {
        enlarge(image);
    } else if (image.width >= LARGE_SIZE) {
        if (origSizes[image] != null) {
            returnToOriginalSize(image);
        }
    }
}


function enlarge(image) {
    origSizes[image] = image.width;
    image.style.width = LARGE_SIZE;
    image.style.cursor = 'zoom-out';
}

function returnToOriginalSize(image) {
    image.style.width = origSizes[image];
    image.style.cursor = 'zoom-in';
}