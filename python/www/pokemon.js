$( document ).ready( function() {
  
  reference = []
  $.get('conversionCenter', function(data) {
    data = data.split(' ');
    reference['x'] = parseInt(data[0], 10);
    reference['y'] = parseInt(data[1], 10);
  });


  setInterval(function() {
    $.get('currentPosition', function(data) {
      data = data.split(' ');
      x = parseInt(data[0], 10);
      y = parseInt(data[1], 10);
      
      var player = $('.player').first();
      player.css({
        'top': parseInt('' + (250 / reference['y'] * y)),
        'left': parseInt('' + (500 / reference['x'] * x))
      });
      
      time = moment().format('MMMM Do YYYY, hh:mm:ss');
      $('.time').html(time);
    });
    
    $.get('currentTime', function(data) {
      data = data.split(' ');
      day = data[0];
      hour = data[1];
      minute = data[2];
      second = data[3];

      $('.container.ingameTime').html(day + "d " + hour + "h " + minute + "m " + second + "s");
    });

  }, 4000);
 
  setInterval(function() {
    $.get('conversionCenter', function(data) {
      data = data.split(' ');
      reference['x'] = parseInt(data[0], 10);
      reference['y'] = parseInt(data[1], 10);

      $('.container').css('background', 'url("mapcrop.png?' + moment().format('X') + '") 0 0 no-repeat');

    });
  }, 450000);  
  
});
